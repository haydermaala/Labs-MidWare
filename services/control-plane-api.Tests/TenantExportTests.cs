using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>Tenant data export (P7, §10.3): the exporter gathers the tenant record, its
/// fleet, each gateway's current config, and the audit trail; unknown tenants yield null.</summary>
public sealed class TenantExportTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<MemberView> NoMembers = [];
    private static readonly IReadOnlyList<InvitationView> NoInvites = [];

    [Fact]
    public void Build_Gathers_Tenant_People_Subscription_Fleet_Config_And_Audit()
    {
        var store = new InMemoryControlPlaneStore(TimeProvider.System);
        var tenant = store.CreateTenant("Export Co");
        var token = store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15));
        var gw = store.Enroll(token!.Token, "edge-1")!;
        store.PublishConfig(tenant.Id, gw.GatewayId, "{\"k\":1}");

        // People + subscription are gathered by the caller and passed in.
        var members = new List<MemberView>
            { new("usr_1", "owner@example.test", "owner", At, Active: true) };
        var invitations = new List<InvitationView>
            { new("inv_1", "pending@example.test", "member", At.AddDays(7), "pending") };
        var subscription = new SubscriptionView("laboratory", "active", At.AddDays(30), CancelAtPeriodEnd: false);

        var export = TenantExporter.Build(store, At, tenant.Id, members, invitations, subscription)!;

        Assert.Equal(tenant.Id, export.Tenant.Id);
        Assert.Equal(At, export.ExportedAt);
        Assert.Contains(export.Members, m => m.UserId == "usr_1");
        Assert.Contains(export.Invitations, i => i.Id == "inv_1");
        Assert.Equal("laboratory", export.Subscription?.PlanId);
        Assert.Contains(export.Gateways, g => g.Id == gw.GatewayId);
        // The gateway's current config is captured alongside it.
        Assert.Equal("{\"k\":1}", export.Configs.Single(c => c.GatewayId == gw.GatewayId).Config?.SettingsJson);
        // The audit trail is included (tenant.created + enrollment + config publish, at least).
        Assert.NotEmpty(export.Audit);
    }

    [Fact]
    public void Build_Returns_Null_For_An_Unknown_Tenant()
    {
        var store = new InMemoryControlPlaneStore(TimeProvider.System);
        Assert.Null(TenantExporter.Build(store, At, "ten_ghost", NoMembers, NoInvites, subscription: null));
    }
}
