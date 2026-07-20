// Billing domain (LabConnect Phase E): plan catalog, per-tenant subscription,
// and server-authoritative entitlements.
//
// Design constraints:
// - Entitlements are computed on the server from the tenant's subscription; the
//   client never asserts what it may do. Enforcement (e.g. gateway quota) reads
//   the entitlement, not the request.
// - No prices live here. Money is the payment provider's concern; a plan's
//   provider price id is supplied by configuration and is never published from
//   code (the pricing gate). Plans here define *entitlements*, not rates.
// - Provider-agnostic: IBillingProvider is the seam. A fake backs dev/tests; a
//   Stripe adapter is added behind the same interface when keys are supplied.
// - No card data is ever stored or handled; only provider ids and status.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>A plan's entitlement definition. Quotas/features are provisional
/// defaults, tunable via configuration; they are the enforcement contract, not
/// published pricing.</summary>
public sealed record Plan(string Id, string Name, int GatewayQuota, IReadOnlySet<string> Features)
{
    /// <summary>Whether a gateway count is within this plan's quota (-1 = unlimited).</summary>
    public bool AllowsGatewayCount(int count) => GatewayQuota < 0 || count < GatewayQuota;
}

/// <summary>The built-in plan catalog. Ids align with the public pricing tiers;
/// "trial" is the default when a tenant has no active subscription.</summary>
public static class Plans
{
    public const string Trial = "trial";
    public const string Pilot = "pilot";
    public const string Laboratory = "laboratory";
    public const string Network = "network";

    private static readonly Dictionary<string, Plan> Catalog = new(StringComparer.Ordinal)
    {
        [Trial] = new(Trial, "Trial", GatewayQuota: 2, Features: Set()),
        [Pilot] = new(Pilot, "Pilot", GatewayQuota: 5, Features: Set()),
        [Laboratory] = new(Laboratory, "Laboratory", GatewayQuota: 25, Features: Set("bidirectional")),
        [Network] = new(Network, "Network", GatewayQuota: -1, Features: Set("bidirectional", "sso")),
    };

    private static HashSet<string> Set(params string[] features) =>
        new(features, StringComparer.Ordinal);

    /// <summary>All plans, in catalog order.</summary>
    public static IReadOnlyList<Plan> All => [Catalog[Trial], Catalog[Pilot], Catalog[Laboratory], Catalog[Network]];

    /// <summary>Resolve a plan id to its definition, falling back to Trial.</summary>
    public static Plan Resolve(string? planId) =>
        planId is not null && Catalog.TryGetValue(planId, out var plan) ? plan : Catalog[Trial];

    public static bool IsKnown(string planId) => Catalog.ContainsKey(planId);
}

/// <summary>Subscription lifecycle states (a superset compatible with Stripe's).</summary>
public static class SubscriptionStatus
{
    public const string Trialing = "trialing";
    public const string Active = "active";
    public const string PastDue = "past_due";
    public const string Canceled = "canceled";

    /// <summary>Whether a status grants the plan's paid entitlements.</summary>
    public static bool IsEntitled(string status) => status is Trialing or Active or PastDue;
}

/// <summary>Server-computed entitlements for a tenant.</summary>
public sealed record Entitlements(
    string PlanId,
    string PlanName,
    string Status,
    int GatewayQuota,
    IReadOnlyCollection<string> Features,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd);

/// <summary>Public view of a tenant's subscription (no provider secrets).</summary>
public sealed record SubscriptionView(
    string PlanId,
    string Status,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd);

/// <summary>Computes and persists per-tenant subscriptions + entitlements.</summary>
public sealed class BillingService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public BillingService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    private void Audit(AppDbContext db, string kind, string tenantId, string detail) =>
        db.Audit.Add(new AuditEntity { At = _clock.GetUtcNow(), Kind = kind, TenantId = tenantId, Detail = detail });

    private static SubscriptionEntity? Find(AppDbContext db, string tenantId) =>
        db.Subscriptions.FirstOrDefault(s => s.TenantId == tenantId);

    /// <summary>The tenant's entitlements. With no subscription (or a canceled
    /// one) the tenant falls back to the Trial plan.</summary>
    public Entitlements EntitlementsFor(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        var sub = db.Subscriptions.AsNoTracking().FirstOrDefault(s => s.TenantId == tenantId);
        var entitledPlanId = sub is not null && SubscriptionStatus.IsEntitled(sub.Status) ? sub.PlanId : Plans.Trial;
        var plan = Plans.Resolve(entitledPlanId);
        return new Entitlements(
            plan.Id, plan.Name,
            sub?.Status ?? SubscriptionStatus.Trialing,
            plan.GatewayQuota, plan.Features.ToList(),
            sub?.CurrentPeriodEnd, sub?.CancelAtPeriodEnd ?? false);
    }

    /// <summary>The tenant's subscription view, or null if none exists yet.</summary>
    public SubscriptionView? SubscriptionFor(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        var sub = db.Subscriptions.AsNoTracking().FirstOrDefault(s => s.TenantId == tenantId);
        return sub is null ? null
            : new SubscriptionView(sub.PlanId, sub.Status, sub.CurrentPeriodEnd, sub.CancelAtPeriodEnd);
    }

    /// <summary>Whether the tenant may enroll another gateway under its plan quota.</summary>
    public bool CanAddGateway(string tenantId, int currentGatewayCount) =>
        Plans.Resolve(EntitlementsFor(tenantId).PlanId).AllowsGatewayCount(currentGatewayCount);

    /// <summary>
    /// Apply a subscription state (from a provider webhook or the fake provider).
    /// Idempotent by (tenant, provider subscription id); creates or updates the row.
    /// </summary>
    public void UpsertSubscription(
        string tenantId, string planId, string status,
        string? providerCustomerId, string? providerSubscriptionId,
        DateTimeOffset? currentPeriodEnd, bool cancelAtPeriodEnd)
    {
        using var db = _factory.CreateDbContext();
        UpsertInto(db, tenantId, planId, status, providerCustomerId, providerSubscriptionId, currentPeriodEnd, cancelAtPeriodEnd);
        db.SaveChanges();
    }

    private void UpsertInto(
        AppDbContext db, string tenantId, string planId, string status,
        string? providerCustomerId, string? providerSubscriptionId,
        DateTimeOffset? currentPeriodEnd, bool cancelAtPeriodEnd)
    {
        var sub = Find(db, tenantId);
        if (sub is null)
        {
            sub = new SubscriptionEntity { Id = Ids.New("sub"), TenantId = tenantId, CreatedAt = _clock.GetUtcNow() };
            db.Subscriptions.Add(sub);
        }
        sub.PlanId = planId;
        sub.Status = status;
        sub.ProviderCustomerId = providerCustomerId ?? sub.ProviderCustomerId;
        sub.ProviderSubscriptionId = providerSubscriptionId ?? sub.ProviderSubscriptionId;
        sub.CurrentPeriodEnd = currentPeriodEnd;
        sub.CancelAtPeriodEnd = cancelAtPeriodEnd;
        sub.UpdatedAt = _clock.GetUtcNow();
        Audit(db, "billing.subscription_updated", tenantId, $"{planId}:{status}");
    }

    /// <summary>
    /// Apply a verified provider webhook event exactly once. The event id is
    /// recorded in billing_events (UNIQUE on ProviderEventId); a duplicate delivery
    /// — whether seen before or racing a concurrent one — is a no-op. Returns true
    /// when this call applied the event, false when it was a replay/duplicate.
    /// </summary>
    public bool TryApplyProviderEvent(ProviderSubscriptionEvent ev)
    {
        using var db = _factory.CreateDbContext();

        // Fast path: an already-processed id is a replay — do nothing.
        if (db.BillingEvents.Any(e => e.ProviderEventId == ev.EventId))
        {
            return false;
        }

        db.BillingEvents.Add(new BillingEventEntity
        {
            Id = Ids.New("evt"),
            ProviderEventId = ev.EventId,
            TenantId = ev.TenantId,
            ReceivedAt = _clock.GetUtcNow(),
        });
        UpsertInto(db, ev.TenantId, ev.PlanId, ev.Status, ev.CustomerId, ev.SubscriptionId, ev.CurrentPeriodEnd, ev.CancelAtPeriodEnd);

        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost a race on the unique ProviderEventId index — another delivery of
            // the same event won. Idempotent by construction: treat as a no-op.
            return false;
        }
    }
}
