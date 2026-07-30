using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>Platform overview aggregation (§13.1): counts by lifecycle state and plan,
/// plus the past-due payment-health signal.</summary>
public sealed class PlatformOverviewTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static Tenant T(string id, string status) =>
        new(id, id.ToUpperInvariant(), At, Active: status == nameof(TenantStatus.Active), Status: status);

    private static Entitlements Ent(string planId, string status) =>
        new(planId, planId, status, GatewayQuota: 5, Features: [],
            CurrentPeriodEnd: null, CancelAtPeriodEnd: false, RetentionDays: 30);

    [Fact]
    public void Build_Aggregates_By_Status_Plan_And_PastDue()
    {
        var tenants = new List<Tenant>
        {
            T("t1", nameof(TenantStatus.Active)),
            T("t2", nameof(TenantStatus.Active)),
            T("t3", nameof(TenantStatus.Suspended)),
            T("t4", nameof(TenantStatus.Offboarding)),
        };
        var plans = new Dictionary<string, Entitlements>
        {
            ["t1"] = Ent("laboratory", "active"),
            ["t2"] = Ent("trial", "trialing"),
            ["t3"] = Ent("pilot", "past_due"),
            ["t4"] = Ent("laboratory", "past_due"),
        };

        var overview = PlatformOverviewBuilder.Build(tenants, id => plans[id]);

        Assert.Equal(4, overview.TotalTenants);
        Assert.Equal(2, overview.TenantsByStatus["Active"]);
        Assert.Equal(1, overview.TenantsByStatus["Suspended"]);
        Assert.Equal(1, overview.TenantsByStatus["Offboarding"]);
        Assert.Equal(2, overview.TenantsByPlan["laboratory"]);
        Assert.Equal(1, overview.TenantsByPlan["trial"]);
        Assert.Equal(2, overview.PastDueCount); // t3 + t4
    }

    [Fact]
    public void Build_Of_An_Empty_Registry_Is_All_Zero()
    {
        var overview = PlatformOverviewBuilder.Build([], _ => throw new InvalidOperationException("not called"));
        Assert.Equal(0, overview.TotalTenants);
        Assert.Empty(overview.TenantsByStatus);
        Assert.Equal(0, overview.PastDueCount);
    }
}
