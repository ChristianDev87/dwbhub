namespace DwbHub.Application.Setup;

/// <summary>
/// Discriminated union for the result of the setup wizard completion attempt.
/// </summary>
public abstract record SetupOutcome
{
    /// <summary>Setup completed successfully.</summary>
    /// <param name="TenantId">Internal id of the newly created tenant.</param>
    /// <param name="TenantSlug">URL slug of the new tenant.</param>
    /// <param name="OwnerUserId">Internal id of the newly created owner user.</param>
    /// <param name="VerificationEmailSent">Whether the verification email was sent without error.</param>
    public sealed record Success(long TenantId, string TenantSlug, long OwnerUserId, bool VerificationEmailSent) : SetupOutcome;
    /// <summary>Bootstrap token hash did not match the stored lock row hash.</summary>
    public sealed record InvalidToken : SetupOutcome;
    /// <summary>Setup was already completed; the wizard is permanently locked.</summary>
    public sealed record AlreadyCompleted : SetupOutcome;
    /// <summary>One or more request fields failed validation (locale, slug, required strings).</summary>
    /// <param name="Details">Human-readable explanation of the first failing field.</param>
    public sealed record InvalidRequest(string Details) : SetupOutcome;
    /// <summary>Owner password does not meet the minimum strength requirement.</summary>
    public sealed record WeakPassword : SetupOutcome;
    /// <summary>Tenant slug is already in use by another tenant.</summary>
    public sealed record SlugInUse : SetupOutcome;
}
