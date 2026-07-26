// Tenant data export (P7, prompt §10.3 — the "export" step of offboarding, and a
// standalone platform capability). Gathers a tenant's control-plane data — the tenant
// record, its fleet, each gateway's current config, and the audit trail — into a single
// serialisable artifact a platform operator can hand back before deletion.
//
// Composition only: each source is one of the store's existing tenant-scoped reads, so
// the export respects the same RLS boundary as everything else.

namespace ControlPlane.Api;

/// <summary>A gateway paired with its current published config (null if none).</summary>
public sealed record GatewayConfigExport(string GatewayId, ConfigView? Config);

/// <summary>A tenant's exported control-plane data: the tenant record, its people
/// (members + open invitations), its subscription, its fleet + config, and its audit
/// trail — the offboarding "inventory" a platform operator hands back (§10.3).</summary>
public sealed record TenantExport(
    Tenant Tenant,
    IReadOnlyList<MemberView> Members,
    IReadOnlyList<InvitationView> Invitations,
    SubscriptionView? Subscription,
    IReadOnlyList<GatewayView> Gateways,
    IReadOnlyList<GatewayConfigExport> Configs,
    IReadOnlyList<AuditEvent> Audit,
    DateTimeOffset ExportedAt);

/// <summary>Builds a <see cref="TenantExport"/>. Fleet/config/audit come from the store's
/// tenant-scoped reads; the people + subscription are gathered by the caller (from the
/// membership + billing services) and passed in, keeping this a pure composition.</summary>
public static class TenantExporter
{
    /// <summary>Gather the tenant's data, or null if the tenant is unknown.</summary>
    public static TenantExport? Build(
        IControlPlaneStore store, DateTimeOffset at, string tenantId,
        IReadOnlyList<MemberView> members,
        IReadOnlyList<InvitationView> invitations,
        SubscriptionView? subscription)
    {
        var tenant = store.FindTenant(tenantId);
        if (tenant is null)
        {
            return null;
        }
        var gateways = store.GatewaysFor(tenantId).ToList();
        var configs = gateways
            .Select(g => new GatewayConfigExport(g.Id, store.CurrentConfig(tenantId, g.Id)))
            .ToList();
        var audit = store.AuditFor(tenantId).ToList();
        return new TenantExport(tenant, members, invitations, subscription, gateways, configs, audit, at);
    }
}
