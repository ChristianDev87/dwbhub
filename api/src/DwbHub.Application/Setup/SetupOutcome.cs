namespace DwbHub.Application.Setup;

public abstract record SetupOutcome
{
    public sealed record Success(long TenantId, string TenantSlug, long OwnerUserId, bool VerificationEmailSent) : SetupOutcome;
    public sealed record InvalidToken : SetupOutcome;
    public sealed record AlreadyCompleted : SetupOutcome;
    public sealed record InvalidRequest(string Details) : SetupOutcome;
    public sealed record WeakPassword : SetupOutcome;
    public sealed record SlugInUse : SetupOutcome;
}
