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
