namespace DwbHub.Application.Auth;

/// <summary>Rendered email ready to hand off to <see cref="IEmailSender"/>.</summary>
/// <param name="ToAddress">Recipient email address.</param>
/// <param name="Subject">Email subject line.</param>
/// <param name="HtmlBody">HTML version of the body.</param>
/// <param name="TextBody">Plain-text fallback body (HTML-stripped).</param>
public sealed record EmailMessage(
    string ToAddress,
    string Subject,
    string HtmlBody,
    string TextBody);
