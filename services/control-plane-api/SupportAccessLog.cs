// Source-generated log messages for support-access. Lives outside Program.cs because a
// top-level program cannot host `[LoggerMessage]` partial methods, and the analyzers
// (CA1848/CA1873) require the generated delegates rather than ILogger extension calls.

using Microsoft.Extensions.Logging;

namespace ControlPlane.Api;

/// <summary>
/// Support-access diagnostics. Prompt §13 requires enhanced audit on support access:
/// reaching another tenant's data on the strength of a grant is exactly the event an
/// incident review needs to reconstruct.
///
/// This is a log line rather than an `audit` row on purpose — it fires on every
/// authorized request for the life of the grant, so writing a row per request would
/// flood the tenant's audit trail. The grant's own lifecycle (request, approve/reject,
/// by whom) is already recorded in platform_security_events.
/// </summary>
public static partial class SupportAccessLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "support-access: user {UserId} acted in tenant {TenantId} without membership, "
                + "using an approved support grant (permission {PermissionKey})")]
    public static partial void TenantAccessedViaSupportGrant(
        ILogger logger, string userId, string tenantId, string permissionKey);
}

/// <summary>
/// Break-glass diagnostics. Use of the god-mode admin token is meant to be rare and
/// reviewable, so every call it makes is logged at Warning. If these lines appear in
/// normal operation, the token is still doing routine work and is not yet ready to be
/// withdrawn.
///
/// This log line is for a human tailing the service. It is NOT the retirement gate:
/// the log is a small rolling buffer with no guaranteed retention, so it cannot answer
/// "has the token been used this month?". That question is answered from the
/// append-only platform_security_events row written alongside this line — see
/// Program.cs BreakGlass(...) and scripts/break-glass-watch.sh.
/// </summary>
public static partial class BreakGlassLog
{
    /// <param name="context">
    /// What the token reached — "tenantId:permission.key" for a tenant-scoped call,
    /// "platform:permission.key" for a platform one, or a bare endpoint key for the
    /// direct-guard bootstrap endpoints.
    /// </param>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "break-glass: the platform admin token acted on {Context} "
                + "— no user identity is attached to this action")]
    public static partial void UsedWithAdminToken(ILogger logger, string context);
}
