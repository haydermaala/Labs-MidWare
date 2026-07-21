// Permission catalog (P2). The authoritative, greppable list of fine-grained
// permissions, each a versioned `<domain>.<resource>.<action>` key with metadata
// (risk, step-up/MFA/approval requirements, delegability). This is the data the
// AuthorizationEngine evaluates and, later, the source the `permission_definitions`
// migration seeds for the admin UI.
//
// P2 is deliberately behaviour-preserving: every permission is tagged with the
// legacy role-capability it currently corresponds to (see Memberships.cs `Roles`),
// and the role→permission matrix (RolePermissions) is derived from that same
// capability set — proven equivalent to today's checks by PermissionEngineTests.
// Finer-grained, per-role grants and scopes arrive with roles/scopes in P3.

namespace ControlPlane.Api;

/// <summary>How damaging a permission is if misused — drives review priority and
/// the default step-up requirements.</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>The legacy <c>Roles.Can*</c> capability a permission maps onto. P2
/// re-expresses these coarse capabilities as a catalog without changing who can do
/// what; P3 replaces this mapping with per-permission role grants.</summary>
public enum LegacyCapability
{
    View,
    ManageFleet,
    ManageUsers,
    ManageTenant,
    ManageBilling,
}

/// <summary>One catalog entry: a permission and its governance metadata.</summary>
/// <param name="Key">Stable id, <c>&lt;domain&gt;.&lt;resource&gt;.&lt;action&gt;</c>.</param>
/// <param name="Risk">Blast radius if misused.</param>
/// <param name="Capability">Legacy capability this maps onto (P2 compatibility).</param>
/// <param name="RequiresMfa">The subject must have satisfied MFA in this session.</param>
/// <param name="RequiresFreshAuth">The subject must have re-authenticated recently (step-up).</param>
/// <param name="RequiresApproval">A second party must approve; cannot be done unilaterally.</param>
/// <param name="Delegable">Whether a custom role may be granted this (P3 delegation limits).</param>
public sealed record PermissionDefinition(
    string Key,
    string Domain,
    string Resource,
    string Action,
    RiskLevel Risk,
    LegacyCapability Capability,
    bool RequiresMfa,
    bool RequiresFreshAuth,
    bool RequiresApproval,
    bool Delegable,
    string Description);

/// <summary>The permission catalog. Fields are greppable, like <see cref="Roles"/>.</summary>
public static class Permissions
{
    private static PermissionDefinition Def(
        string domain, string resource, string action, RiskLevel risk, LegacyCapability capability,
        string description,
        bool requiresMfa = false, bool requiresFreshAuth = false, bool requiresApproval = false,
        bool delegable = true) =>
        new($"{domain}.{resource}.{action}", domain, resource, action, risk, capability,
            requiresMfa, requiresFreshAuth, requiresApproval, delegable, description);

    // ── Fleet: gateways + config ─────────────────────────────────────────────
    public static readonly PermissionDefinition FleetGatewayView =
        Def("fleet", "gateway", "view", RiskLevel.Low, LegacyCapability.View, "List/read gateways in the tenant.");
    public static readonly PermissionDefinition FleetGatewayEnroll =
        Def("fleet", "gateway", "enroll", RiskLevel.Medium, LegacyCapability.ManageFleet, "Issue enrollment tokens / enroll a gateway.");
    public static readonly PermissionDefinition FleetGatewayDecommission =
        Def("fleet", "gateway", "decommission", RiskLevel.High, LegacyCapability.ManageFleet,
            "Decommission a gateway and revoke its credential (irreversible).", requiresFreshAuth: true);
    public static readonly PermissionDefinition FleetConfigView =
        Def("fleet", "config", "view", RiskLevel.Low, LegacyCapability.View, "Read a gateway's published config.");
    public static readonly PermissionDefinition FleetConfigPublish =
        Def("fleet", "config", "publish", RiskLevel.Medium, LegacyCapability.ManageFleet, "Publish a new (non-production) config version.");

    // ── Members + invitations ────────────────────────────────────────────────
    public static readonly PermissionDefinition MembersMemberView =
        Def("members", "member", "view", RiskLevel.Low, LegacyCapability.ManageUsers, "List a tenant's members and roles.");
    public static readonly PermissionDefinition MembersMemberInvite =
        Def("members", "member", "invite", RiskLevel.Medium, LegacyCapability.ManageUsers, "Invite a user into the tenant.");
    public static readonly PermissionDefinition MembersMemberChangeRole =
        Def("members", "member", "change_role", RiskLevel.High, LegacyCapability.ManageUsers,
            "Change a member's role.", requiresFreshAuth: true);
    public static readonly PermissionDefinition MembersMemberRemove =
        Def("members", "member", "remove", RiskLevel.High, LegacyCapability.ManageUsers,
            "Remove a member from the tenant.", requiresFreshAuth: true);
    public static readonly PermissionDefinition MembersInvitationView =
        Def("members", "invitation", "view", RiskLevel.Low, LegacyCapability.ManageUsers, "List pending invitations.");
    public static readonly PermissionDefinition MembersInvitationRevoke =
        Def("members", "invitation", "revoke", RiskLevel.Medium, LegacyCapability.ManageUsers, "Revoke a pending invitation.");

    // ── Tenant lifecycle ─────────────────────────────────────────────────────
    public static readonly PermissionDefinition TenantSettingsView =
        Def("tenant", "settings", "view", RiskLevel.Low, LegacyCapability.View, "Read tenant settings.");
    public static readonly PermissionDefinition TenantRename =
        Def("tenant", "tenant", "rename", RiskLevel.Medium, LegacyCapability.ManageTenant, "Rename the tenant.");
    public static readonly PermissionDefinition TenantDeactivate =
        Def("tenant", "tenant", "deactivate", RiskLevel.Critical, LegacyCapability.ManageTenant,
            "Deactivate the tenant (stops enrollment; retains data).", requiresFreshAuth: true);
    public static readonly PermissionDefinition TenantReactivate =
        Def("tenant", "tenant", "reactivate", RiskLevel.Medium, LegacyCapability.ManageTenant, "Reactivate a deactivated tenant.");

    // ── Billing ──────────────────────────────────────────────────────────────
    public static readonly PermissionDefinition BillingSubscriptionView =
        // Any tenant member may read billing today (endpoint uses CanView); tightening
        // to a billing-only capability is an enforcement-phase decision, not a P2 change.
        Def("billing", "subscription", "view", RiskLevel.Low, LegacyCapability.View, "Read the tenant's subscription + entitlements.");
    public static readonly PermissionDefinition BillingSubscriptionManage =
        Def("billing", "subscription", "manage", RiskLevel.High, LegacyCapability.ManageBilling,
            "Start/change the tenant's plan via checkout.", requiresFreshAuth: true);
    public static readonly PermissionDefinition BillingPortalOpen =
        Def("billing", "portal", "open", RiskLevel.Medium, LegacyCapability.ManageBilling, "Open the provider billing portal.");

    // ── Audit ────────────────────────────────────────────────────────────────
    public static readonly PermissionDefinition AuditLogView =
        Def("audit", "log", "view", RiskLevel.Medium, LegacyCapability.View, "Read the tenant's audit trail.");

    /// <summary>Every permission, in catalog order.</summary>
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        FleetGatewayView, FleetGatewayEnroll, FleetGatewayDecommission, FleetConfigView, FleetConfigPublish,
        MembersMemberView, MembersMemberInvite, MembersMemberChangeRole, MembersMemberRemove,
        MembersInvitationView, MembersInvitationRevoke,
        TenantSettingsView, TenantRename, TenantDeactivate, TenantReactivate,
        BillingSubscriptionView, BillingSubscriptionManage, BillingPortalOpen,
        AuditLogView,
    ];

    private static readonly IReadOnlyDictionary<string, PermissionDefinition> Index =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);

    /// <summary>Resolve a permission by key, or null if unknown.</summary>
    public static PermissionDefinition? Find(string key) => Index.GetValueOrDefault(key);
}

/// <summary>
/// The role→permission matrix, held as data (each role's set of legacy
/// capabilities) rather than scattered <c>Roles.Can*</c> calls. Proven equivalent
/// to those predicates by PermissionEngineTests, so P2 changes no one's access.
/// P3 replaces this with explicit per-permission grants + custom roles + scopes.
/// </summary>
public static class RolePermissions
{
    private static readonly Dictionary<string, IReadOnlySet<LegacyCapability>> ByRole =
        new Dictionary<string, IReadOnlySet<LegacyCapability>>(StringComparer.Ordinal)
        {
            [Roles.Owner] = Set(LegacyCapability.View, LegacyCapability.ManageFleet, LegacyCapability.ManageUsers, LegacyCapability.ManageTenant, LegacyCapability.ManageBilling),
            [Roles.TenantAdmin] = Set(LegacyCapability.View, LegacyCapability.ManageFleet, LegacyCapability.ManageUsers),
            [Roles.LabAdmin] = Set(LegacyCapability.View, LegacyCapability.ManageFleet),
            [Roles.Technician] = Set(LegacyCapability.View),
            [Roles.MappingReviewer] = Set(LegacyCapability.View),
            [Roles.ClinicalApprover] = Set(LegacyCapability.View),
            [Roles.BillingAdmin] = Set(LegacyCapability.View, LegacyCapability.ManageBilling),
            [Roles.Auditor] = Set(LegacyCapability.View),
            [Roles.ReadOnly] = Set(LegacyCapability.View),
        };

    private static HashSet<LegacyCapability> Set(params LegacyCapability[] caps) => new(caps);

    /// <summary>Whether a baseline role grants a permission (before step-up/approval).</summary>
    public static bool Grants(string role, PermissionDefinition permission) =>
        ByRole.TryGetValue(role, out var caps) && caps.Contains(permission.Capability);

    /// <summary>Every permission a role grants (before step-up/approval).</summary>
    public static IReadOnlyCollection<PermissionDefinition> GrantedTo(string role) =>
        Permissions.All.Where(p => Grants(role, p)).ToList();
}
