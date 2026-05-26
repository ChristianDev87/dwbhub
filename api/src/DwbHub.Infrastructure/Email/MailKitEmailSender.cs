using DwbHub.Application.Auth;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DwbHub.Infrastructure.Email;

/// <summary>
/// Synchronous-semantics SMTP sender via MailKit. Dev/CI talks to Mailpit on
/// port 1025 (plaintext); production talks to a real relay (STARTTLS auto-detected
/// by MailKit). 10s timeout on both connect and send.
/// </summary>
public sealed class MailKitEmailSender : IEmailSender
{
    private const int TimeoutMs = 10_000;

    private readonly string _host;
    private readonly int _port;
    private readonly MailboxAddress _from;

    /// <summary>
    /// Initialise the sender with SMTP connection parameters.
    /// </summary>
    /// <param name="host">SMTP server hostname (e.g. <c>localhost</c> or the relay FQDN).</param>
    /// <param name="port">SMTP port (e.g. 1025 for Mailpit, 587 for STARTTLS).</param>
    /// <param name="from">RFC 5321 sender address (<c>display name &lt;addr&gt;</c> or bare address).</param>
    /// <exception cref="InvalidOperationException">Thrown when any required parameter is missing or invalid.</exception>
    public MailKitEmailSender(string host, int port, string from)
    {
        _host = !string.IsNullOrWhiteSpace(host)
            ? host
            : throw new InvalidOperationException("DWBHUB_SMTP_HOST is required.");
        _port = port > 0
            ? port
            : throw new InvalidOperationException("DWBHUB_SMTP_PORT is required and must be > 0.");
        _from = !string.IsNullOrWhiteSpace(from)
            ? MailboxAddress.Parse(from)
            : throw new InvalidOperationException("DWBHUB_SMTP_FROM is required.");
    }

    /// <inheritdoc/>
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var mime = new MimeMessage();
        mime.From.Add(_from);
        mime.To.Add(MailboxAddress.Parse(message.ToAddress));
        mime.Subject = message.Subject;

        var builder = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        };
        mime.Body = builder.ToMessageBody();

        using var client = new SmtpClient
        {
            Timeout = TimeoutMs,
        };

        await client.ConnectAsync(_host, _port, SecureSocketOptions.Auto, ct).ConfigureAwait(false);
        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
    }
}
