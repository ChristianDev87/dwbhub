namespace DwbHub.Application.Auth;

/// <summary>
/// Delivers a rendered <see cref="EmailMessage"/> to its recipient.
/// </summary>
public interface IEmailSender
{
    /// <summary>Send the email. Throws on delivery failure — callers decide whether to propagate or swallow.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}
