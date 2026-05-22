namespace DwbHub.Application.Auth;

public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody);
