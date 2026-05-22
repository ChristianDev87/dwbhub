namespace DwbHub.Application.Auth;

public abstract record ResetConfirmOutcome
{
    public sealed record Success(int SessionsRevoked) : ResetConfirmOutcome;
    public sealed record Invalid                      : ResetConfirmOutcome;
    public sealed record WeakPassword                 : ResetConfirmOutcome;
}
