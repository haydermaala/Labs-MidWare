// Email provider abstraction (ADR 0017: Titan SMTP first, API providers later).
// The interface is the seam: SmtpEmailSender is selected when Smtp:Host is
// configured; otherwise NullEmailSender keeps dev/tests email-free. Message
// bodies carry only links/status — never passwords, PHI, or result data.

using System.Net;
using System.Net.Mail;

namespace ControlPlane.Api;

/// <summary>An outbound transactional email (text + optional HTML body).</summary>
public sealed record OutboundEmail(string To, string Subject, string TextBody, string? HtmlBody);

/// <summary>Provider seam for transactional email.</summary>
public interface IEmailSender
{
    Task SendAsync(OutboundEmail email, CancellationToken ct = default);
}

/// <summary>Dev/test sink: records nothing but a redacted log line.</summary>
public sealed partial class NullEmailSender : IEmailSender
{
    private readonly ILogger<NullEmailSender> _log;
    public NullEmailSender(ILogger<NullEmailSender> log) => _log = log;

    // Recipient is masked; bodies (which contain single-use links) are never logged.
    [LoggerMessage(Level = LogLevel.Information, Message = "email sink (no SMTP configured): subject={Subject}")]
    private static partial void LogSunk(ILogger logger, string subject);

    public Task SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        LogSunk(_log, email.Subject);
        return Task.CompletedTask;
    }
}

/// <summary>Standard SMTP sender (Titan). Config: Smtp:Host, Smtp:Port (587),
/// Smtp:Username, Smtp:Password, Smtp:From. Credentials live only in the
/// environment/secret store and are never returned by any endpoint.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    public SmtpEmailSender(IConfiguration config) => _config = config;

    public async Task SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var host = _config["Smtp:Host"]!;
        var port = int.TryParse(_config["Smtp:Port"], out var p) ? p : 587;
        var from = _config["Smtp:From"] ?? _config["Smtp:Username"]!;

        using var message = new MailMessage(from, email.To, email.Subject, email.TextBody);
        if (email.HtmlBody is not null)
        {
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                email.HtmlBody, null, "text/html"));
        }
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_config["Smtp:Username"], _config["Smtp:Password"]),
        };
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>Minimal accessible templates: plain text + simple semantic HTML.</summary>
public static class EmailTemplates
{
    public static OutboundEmail VerifyEmail(string to, string link) => new(
        to,
        "Verify your LabConnect email address",
        $"Confirm this email address for your LabConnect account:\n\n{link}\n\nThis link is single-use and expires in 24 hours. If you did not create an account, ignore this message.",
        $"<p>Confirm this email address for your LabConnect account:</p><p><a href=\"{link}\">Verify email address</a></p><p>This link is single-use and expires in 24 hours. If you did not create an account, ignore this message.</p>");

    public static OutboundEmail ResetPassword(string to, string link) => new(
        to,
        "Reset your LabConnect password",
        $"Reset the password for your LabConnect account:\n\n{link}\n\nThis link is single-use and expires in 1 hour. If you did not request a reset, ignore this message; your password is unchanged.",
        $"<p>Reset the password for your LabConnect account:</p><p><a href=\"{link}\">Reset password</a></p><p>This link is single-use and expires in 1 hour. If you did not request a reset, ignore this message; your password is unchanged.</p>");
}
