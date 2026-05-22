namespace DwbHub.Application.Auth;

public abstract record VerifyConfirmOutcome
{
    public sealed record Success : VerifyConfirmOutcome;
    public sealed record Invalid : VerifyConfirmOutcome;
}
