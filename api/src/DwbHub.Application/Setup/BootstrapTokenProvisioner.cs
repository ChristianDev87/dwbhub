using DwbHub.Application.Auth;
using DwbHub.Core.Repositories;
using Microsoft.Extensions.Logging;

namespace DwbHub.Application.Setup;

/// <summary>
/// Implements <see cref="IBootstrapTokenProvisioner"/>: decides at startup whether to generate
/// a new bootstrap token, skip silently, or warn about a lost token file.
/// </summary>
public sealed class BootstrapTokenProvisioner(
    ISystemBootstrapLockRepository locks,
    ITokenHasher hasher,
    ITokenGenerator generator,
    IBootstrapTokenWriter writer,
    ILogger<BootstrapTokenProvisioner> logger) : IBootstrapTokenProvisioner
{
    /// <inheritdoc/>
    public async Task ProvisionAsync(CancellationToken ct = default)
    {
        var existing = await locks.LoadAsync(ct).ConfigureAwait(false);

        if (existing is { ConsumedAt: not null })
        {
            logger.LogInformation(
                "[Bootstrap] Setup already completed at {ConsumedAt}. Wizard locked.",
                existing.ConsumedAt);
            return;
        }

        if (existing is { ConsumedAt: null })
        {
            // Lock row pending. Plaintext should still be on disk.
            var fileExists = await writer.ExistsAsync(ct).ConfigureAwait(false);
            if (fileExists)
            {
                logger.LogInformation(
                    "[Bootstrap] Wizard token already provisioned at {Location}.",
                    writer.Location);
            }
            else
            {
                logger.LogWarning(
                    "[Bootstrap] Lock row pending but token file missing at {Location}. " +
                    "The plaintext cannot be recovered. Recovery: " +
                    "`DELETE FROM system_bootstrap_lock` + restart container.",
                    writer.Location);
            }
            return;
        }

        // No lock row exists — generate a fresh token.
        var plaintext = generator.GenerateUrlSafeBase64();
        var hash = hasher.Hash(plaintext);
        await locks.InsertAsync(hash, ct).ConfigureAwait(false);
        await writer.WriteAsync(plaintext, ct).ConfigureAwait(false);

        logger.LogInformation(
            "[Bootstrap] Fresh setup token provisioned. Plaintext written to {Location}. " +
            "Operator should now POST it to /api/setup/complete via the web wizard.",
            writer.Location);
    }
}
