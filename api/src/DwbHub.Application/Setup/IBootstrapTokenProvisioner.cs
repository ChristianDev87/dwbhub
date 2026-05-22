namespace DwbHub.Application.Setup;

/// <summary>
/// One-shot bootstrap-token provisioner. Called once at host startup. Decides
/// whether to generate a new CSPRNG token + persist hash + write plaintext to
/// disk, or skip silently (already consumed or already pending with file
/// present), or warn (pending lock row but token file lost — recovery required).
/// </summary>
public interface IBootstrapTokenProvisioner
{
    Task ProvisionAsync(CancellationToken ct = default);
}
