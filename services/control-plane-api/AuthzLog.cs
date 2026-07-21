// Source-generated log messages for the authorization engine's shadow phase
// (ADR 0019). Kept in a partial class so the same LoggerMessage pattern as
// Email/MailDelivery applies to the top-level Program.cs call site.

using Microsoft.Extensions.Logging;

namespace ControlPlane.Api;

internal static partial class AuthzLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "authz shadow mismatch: perm={Permission} role={Role} tenant={Tenant} legacy={Legacy} engine={Engine} reason={Reason}")]
    public static partial void ShadowMismatch(
        ILogger logger, string permission, string role, string tenant, bool legacy, bool engine, string reason);
}
