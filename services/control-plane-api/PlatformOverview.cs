// Platform overview aggregates (P6/P7, prompt §13.1 "Overview dashboard"): tenant
// counts by lifecycle state and by plan, plus a payment-health signal (how many tenants
// are past-due). These span tenants, so the endpoint reads the registry under the
// platform scope and resolves each tenant's plan via the billing service.
//
// The aggregation is a pure function of the tenant list + a per-tenant entitlement
// lookup, so it is testable without a database.

namespace ControlPlane.Api;

/// <summary>At-a-glance platform health for the super-admin console (§13.1).</summary>
public sealed record PlatformOverview(
    int TotalTenants,
    IReadOnlyDictionary<string, int> TenantsByStatus,
    IReadOnlyDictionary<string, int> TenantsByPlan,
    int PastDueCount);

/// <summary>Builds a <see cref="PlatformOverview"/> from the tenant registry and a
/// per-tenant entitlement resolver.</summary>
public static class PlatformOverviewBuilder
{
    public static PlatformOverview Build(
        IReadOnlyCollection<Tenant> tenants, Func<string, Entitlements> entitlementsFor)
    {
        var byStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        var byPlan = new Dictionary<string, int>(StringComparer.Ordinal);
        var pastDue = 0;
        foreach (var tenant in tenants)
        {
            byStatus[tenant.Status] = byStatus.GetValueOrDefault(tenant.Status) + 1;
            var entitlements = entitlementsFor(tenant.Id);
            byPlan[entitlements.PlanId] = byPlan.GetValueOrDefault(entitlements.PlanId) + 1;
            if (entitlements.Status == SubscriptionStatus.PastDue)
            {
                pastDue++;
            }
        }
        return new PlatformOverview(tenants.Count, byStatus, byPlan, pastDue);
    }
}
