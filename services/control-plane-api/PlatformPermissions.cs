// Platform (super-admin) permission catalog (P6, program prompt §6.1). A SEPARATE
// namespace from the tenant permission catalog (Permissions.cs): platform access is
// never inferred from tenant roles and vice-versa. These gate the internal
// super-admin surface (/api/platform/...), which the named platform roles
// (PlatformRoles) are granted against — replacing the single god-mode admin token.
//
// Like the tenant catalog, fields are greppable and carry step-up metadata; unlike
// it, there is no LegacyCapability bridge (the platform side is greenfield).

namespace ControlPlane.Api;

/// <summary>One platform permission: <c>platform.&lt;resource&gt;.&lt;action&gt;</c>
/// plus the assurance it demands.</summary>
public sealed record PlatformPermissionDefinition(
    string Key, string Resource, string Action, RiskLevel Risk,
    bool RequiresMfa, bool RequiresFreshAuth, string Description);

/// <summary>The platform permission catalog. Fields are greppable, like
/// <see cref="Permissions"/>.</summary>
public static class PlatformPermissions
{
    private static PlatformPermissionDefinition Def(
        string resource, string action, RiskLevel risk, string description,
        bool requiresMfa = false, bool requiresFreshAuth = false) =>
        new($"platform.{resource}.{action}", resource, action, risk, requiresMfa, requiresFreshAuth, description);

    // ── Tenant lifecycle (operations) ────────────────────────────────────────
    public static readonly PlatformPermissionDefinition TenantRead =
        Def("tenant", "read", RiskLevel.Low, "List/read tenants across the platform (registry only, no clinical data).");
    public static readonly PlatformPermissionDefinition TenantProvision =
        Def("tenant", "provision", RiskLevel.High, "Provision a new tenant.", requiresFreshAuth: true);
    public static readonly PlatformPermissionDefinition TenantSuspend =
        Def("tenant", "suspend", RiskLevel.High, "Suspend/reactivate a tenant.", requiresFreshAuth: true);
    public static readonly PlatformPermissionDefinition TenantOffboard =
        Def("tenant", "offboard", RiskLevel.Critical,
            "Offboard/delete a tenant (irreversible; two-party approval).", requiresMfa: true, requiresFreshAuth: true);
    public static readonly PlatformPermissionDefinition TenantExport =
        Def("tenant", "export", RiskLevel.High,
            "Export a tenant's control-plane data (fleet, config, audit) as an artifact.", requiresFreshAuth: true);

    // ── Subscription (billing) ───────────────────────────────────────────────
    public static readonly PlatformPermissionDefinition SubscriptionManage =
        Def("subscription", "manage", RiskLevel.High, "Manage a tenant's plan/subscription.", requiresFreshAuth: true);

    // ── Users ────────────────────────────────────────────────────────────────
    public static readonly PlatformPermissionDefinition UserRead =
        Def("user", "read", RiskLevel.Low, "Read global user records (no tenant business data).");

    /// <summary>
    /// Create an operator account. Critical, and Root-Owner-only, because the creator
    /// chooses the initial password — so this is effectively the power to authenticate
    /// AS the new account. It is a bootstrap primitive, not routine onboarding: tenant
    /// users arrive through the invitation flow (which binds tenant, role and intended
    /// recipient to a single-use token), so nothing day-to-day needs this.
    /// Replaces the god-mode token's POST /api/admin/users.
    /// </summary>
    public static readonly PlatformPermissionDefinition UserCreate =
        Def("user", "create", RiskLevel.Critical,
            "Create an operator account (bootstrap; tenant users come via invitations).",
            requiresMfa: true, requiresFreshAuth: true);

    /// <summary>
    /// Seat the FIRST owner of an ownerless tenant. Critical and Root-Owner-only for
    /// the same reason as <see cref="UserCreate"/> — the two together are tenant
    /// takeover, so they must never be split across roles that one person could hold.
    /// The endpoint additionally refuses any tenant that already has an active owner,
    /// so this can rescue a stranded tenant but cannot inject an operator into a
    /// working one. Replaces the god-mode token's POST /api/admin/memberships.
    /// </summary>
    public static readonly PlatformPermissionDefinition MembershipSeed =
        Def("membership", "seed", RiskLevel.Critical,
            "Seat the first owner of an ownerless tenant (bootstrap only).",
            requiresMfa: true, requiresFreshAuth: true);

    // ── Support access (time-limited tenant sessions) ────────────────────────
    public static readonly PlatformPermissionDefinition SupportRequest =
        Def("support", "request", RiskLevel.Medium, "Request a time-limited tenant support-access grant.");
    public static readonly PlatformPermissionDefinition SupportApprove =
        Def("support", "approve", RiskLevel.High,
            "Approve a support-access grant (distinct party from the requester).", requiresFreshAuth: true);

    // ── Feature flags / releases / jobs ──────────────────────────────────────
    public static readonly PlatformPermissionDefinition FeatureFlagManage =
        Def("feature_flag", "manage", RiskLevel.Medium, "Manage platform feature flags.");
    public static readonly PlatformPermissionDefinition ReleaseManage =
        Def("release", "manage", RiskLevel.High, "Manage release channels / roll back.", requiresFreshAuth: true);
    public static readonly PlatformPermissionDefinition JobManage =
        Def("job", "manage", RiskLevel.Medium, "Manage platform background jobs.");

    // ── Security + audit ─────────────────────────────────────────────────────
    public static readonly PlatformPermissionDefinition SecurityEventRead =
        Def("security_event", "read", RiskLevel.Medium, "Read platform security events.");
    public static readonly PlatformPermissionDefinition AuditRead =
        Def("audit", "read", RiskLevel.Low, "Read the platform audit trail / compliance evidence.");

    // ── Platform role administration (Root Owner only) ───────────────────────
    public static readonly PlatformPermissionDefinition RoleManage =
        Def("role", "manage", RiskLevel.Critical,
            "Assign/revoke platform roles (the most privileged operation).",
            requiresMfa: true, requiresFreshAuth: true);

    /// <summary>Every platform permission, in catalog order.</summary>
    public static readonly IReadOnlyList<PlatformPermissionDefinition> All =
    [
        TenantRead, TenantProvision, TenantSuspend, TenantOffboard, TenantExport,
        SubscriptionManage,
        UserRead, UserCreate, MembershipSeed,
        SupportRequest, SupportApprove,
        FeatureFlagManage, ReleaseManage, JobManage,
        SecurityEventRead, AuditRead,
        RoleManage,
    ];

    private static readonly IReadOnlyDictionary<string, PlatformPermissionDefinition> Index =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    /// <summary>Resolve a platform permission by key, or null if unknown.</summary>
    public static PlatformPermissionDefinition? Find(string key) => Index.GetValueOrDefault(key);
}
