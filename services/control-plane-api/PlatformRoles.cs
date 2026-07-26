// Platform (super-admin) roles + role→permission matrix (P6, program prompt §7.2).
// There is deliberately NO single all-powerful routine role: operational access is
// divided among named roles. The Root Owner is a break-glass role (all permissions,
// hardware MFA + fresh auth + reason + alerting) assigned to the minimum number of
// humans, not used for routine work. A platform role assignment is distinct from a
// tenant membership and is never inferred from tenant roles.

namespace ControlPlane.Api;

/// <summary>The named platform roles. Keys are stable identifiers.</summary>
public static class PlatformRoles
{
    public const string RootOwner = "platform-root-owner";
    public const string OperationsAdmin = "platform-operations-admin";
    public const string SupportEngineer = "platform-support-engineer";
    public const string BillingAdmin = "platform-billing-admin";
    public const string SecurityAdmin = "platform-security-admin";
    public const string Auditor = "platform-auditor";
    public const string ReleaseManager = "platform-release-manager";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        RootOwner, OperationsAdmin, SupportEngineer, BillingAdmin, SecurityAdmin, Auditor, ReleaseManager,
    };

    /// <summary>The break-glass role: everything, and never for routine work.</summary>
    public static bool IsBreakGlass(string role) => string.Equals(role, RootOwner, StringComparison.Ordinal);
}

/// <summary>The platform role→permission matrix, held as data (like the tenant
/// <see cref="RolePermissions"/>). Root Owner is granted everything; every other
/// role is least-privilege for its function. No role grants clinical payload access
/// (there is no such platform permission — payload access is a tenant-scoped,
/// support-grant-gated concern).</summary>
public static class PlatformRolePermissions
{
    private static readonly Dictionary<string, IReadOnlySet<string>> ByRole =
        new(StringComparer.Ordinal)
        {
            // Root Owner: all permissions (break-glass).
            [PlatformRoles.RootOwner] = PlatformPermissions.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal),

            // Operations: tenant provisioning/suspension/offboarding, releases, jobs, health.
            [PlatformRoles.OperationsAdmin] = Set(
                PlatformPermissions.TenantRead, PlatformPermissions.TenantProvision,
                PlatformPermissions.TenantSuspend, PlatformPermissions.TenantOffboard,
                PlatformPermissions.TenantExport,
                PlatformPermissions.ReleaseManage, PlatformPermissions.JobManage,
                PlatformPermissions.FeatureFlagManage),

            // Support: request time-limited grants; read the tenant registry; export a
            // tenant's data (e.g. to hand back on offboarding). No standing data access.
            [PlatformRoles.SupportEngineer] = Set(
                PlatformPermissions.TenantRead, PlatformPermissions.SupportRequest,
                PlatformPermissions.TenantExport),

            // Billing: subscriptions/plans + registry context. No clinical, no lifecycle.
            [PlatformRoles.BillingAdmin] = Set(
                PlatformPermissions.TenantRead, PlatformPermissions.SubscriptionManage),

            // Security: security events, support-grant approval, audit, user read (incident response).
            [PlatformRoles.SecurityAdmin] = Set(
                PlatformPermissions.SecurityEventRead, PlatformPermissions.SupportApprove,
                PlatformPermissions.AuditRead, PlatformPermissions.UserRead,
                PlatformPermissions.TenantRead),

            // Auditor: read-only audit + compliance evidence.
            [PlatformRoles.Auditor] = Set(
                PlatformPermissions.AuditRead, PlatformPermissions.SecurityEventRead,
                PlatformPermissions.TenantRead),

            // Release: release channels + rollback only.
            [PlatformRoles.ReleaseManager] = Set(PlatformPermissions.ReleaseManage),
        };

    private static HashSet<string> Set(params PlatformPermissionDefinition[] permissions) =>
        permissions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="role"/> grants <paramref name="permissionKey"/>.</summary>
    public static bool Grants(string role, string permissionKey) =>
        ByRole.TryGetValue(role, out var keys) && keys.Contains(permissionKey);

    /// <summary>The permission keys a platform role grants (empty for an unknown role).</summary>
    public static IReadOnlySet<string> PermissionsOf(string role) =>
        ByRole.TryGetValue(role, out var keys) ? keys : new HashSet<string>(StringComparer.Ordinal);
}
