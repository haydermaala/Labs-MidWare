// Transactional email delivery that cannot change what a caller observes.
//
// Endpoints that are deliberately uniform — password reset above all — must
// return the same response whether or not an account exists. Letting a send
// failure propagate breaks that: an existing account would 500 while an unknown
// address still returned 202, so a degraded mail provider silently becomes an
// account-enumeration oracle. Delivery failures are logged (never with the
// recipient) and reported to the caller as a flag where that is useful.

namespace ControlPlane.Api;

/// <summary>Best-effort transactional send with structured failure logging.</summary>
public static partial class MailDelivery
{
    [LoggerMessage(Level = LogLevel.Error, Message = "transactional email failed to send: {Purpose}")]
    private static partial void LogFailure(ILogger logger, string purpose, Exception exception);

    /// <summary>Send an email; returns whether the provider accepted it.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Delivery failure must never alter the caller-visible outcome; every provider error is caught deliberately and logged.")]
    public static async Task<bool> TrySendAsync(
        IEmailSender mail,
        OutboundEmail email,
        string purpose,
        ILogger logger,
        CancellationToken ct = default)
    {
        try
        {
            await mail.SendAsync(email, ct);
            return true;
        }
        catch (Exception ex)
        {
            // The recipient is deliberately absent from the log line.
            LogFailure(logger, purpose, ex);
            return false;
        }
    }
}
