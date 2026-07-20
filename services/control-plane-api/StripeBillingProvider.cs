// Stripe adapter for IBillingProvider (test-mode first). Activated only when
// Stripe:SecretKey is configured; otherwise the fake provider stays in place.
// This is the ONLY file that references the Stripe SDK — the rest of the app
// depends solely on IBillingProvider. No card data ever touches this service;
// Stripe hosts checkout and the portal, and we persist only ids + status.
//
// Plan ↔ price mapping is configuration: Stripe:Prices:{planId} = price id. The
// tenant id rides on the subscription's metadata (set at checkout) so webhook
// events resolve back to the tenant without a customer→tenant lookup table.

using Stripe;

namespace ControlPlane.Api;

public sealed class StripeBillingProvider : IBillingProvider
{
    private readonly string _webhookSecret;
    private readonly string _baseUrl;
    private readonly Dictionary<string, string> _planToPrice;
    private readonly Dictionary<string, string> _priceToPlan;

    public StripeBillingProvider(IConfiguration config)
    {
        // Setting the SDK's global key here means the provider is inert unless the
        // key is present — DI only constructs this when Stripe:SecretKey is set.
        StripeConfiguration.ApiKey = config["Stripe:SecretKey"]
            ?? throw new InvalidOperationException("Stripe:SecretKey is required for the Stripe billing provider.");
        _webhookSecret = config["Stripe:WebhookSecret"] ?? "";
        _baseUrl = (config["ControlPlane:PublicBaseUrl"] ?? "http://localhost:5173").TrimEnd('/');

        var planToPrice = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var planId in new[] { Plans.Pilot, Plans.Laboratory, Plans.Network })
        {
            var priceId = config[$"Stripe:Prices:{planId}"];
            if (!string.IsNullOrWhiteSpace(priceId)) planToPrice[planId] = priceId;
        }
        _planToPrice = planToPrice;
        _priceToPlan = planToPrice.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
    }

    public string Name => "stripe";
    public string SignatureHeaderName => "Stripe-Signature";

    public async Task<ProviderRedirect> CreateCheckoutAsync(string tenantId, string planId, CancellationToken ct = default)
    {
        if (!_planToPrice.TryGetValue(planId, out var priceId))
        {
            throw new InvalidOperationException($"No Stripe price is configured for plan '{planId}'.");
        }
        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            ClientReferenceId = tenantId,
            LineItems = [new Stripe.Checkout.SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions
            {
                // The webhook resolves the tenant + plan from this metadata.
                Metadata = new Dictionary<string, string> { ["tenantId"] = tenantId, ["planId"] = planId },
            },
            SuccessUrl = $"{_baseUrl}/billing?checkout=success",
            CancelUrl = $"{_baseUrl}/billing?checkout=canceled",
        };
        var session = await new Stripe.Checkout.SessionService().CreateAsync(options, cancellationToken: ct);
        return new ProviderRedirect(session.Url);
    }

    public async Task<ProviderRedirect> CreatePortalAsync(string tenantId, string? providerCustomerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(providerCustomerId))
        {
            throw new InvalidOperationException("This tenant has no Stripe customer yet; complete checkout first.");
        }
        var options = new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = providerCustomerId,
            ReturnUrl = $"{_baseUrl}/billing",
        };
        var session = await new Stripe.BillingPortal.SessionService().CreateAsync(options, cancellationToken: ct);
        return new ProviderRedirect(session.Url);
    }

    public ProviderSubscriptionEvent? ParseWebhook(string payload, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(_webhookSecret))
        {
            return null;
        }
        Event stripeEvent;
        try
        {
            // Verifies the HMAC signature and timestamp tolerance (replay guard).
            stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, _webhookSecret);
        }
        catch (StripeException)
        {
            return null;
        }

        // Only subscription lifecycle events change entitlements.
        if (stripeEvent.Data.Object is not Subscription sub)
        {
            return null;
        }

        var tenantId = MetadataValue(sub.Metadata, "tenantId");
        if (tenantId is null)
        {
            return null;
        }
        var priceId = sub.Items?.Data?.Count > 0 ? sub.Items.Data[0].Price?.Id : null;
        var planId = ResolvePlan(priceId, _priceToPlan, MetadataValue(sub.Metadata, "planId"));
        if (planId is null)
        {
            return null;
        }

        var status = MapStatus(stripeEvent.Type, sub.Status);
        DateTimeOffset? periodEnd = PeriodEndOf(sub);
        return new ProviderSubscriptionEvent(
            stripeEvent.Id, tenantId, planId, status, sub.CustomerId, sub.Id, periodEnd, sub.CancelAtPeriodEnd);
    }

    private static string? MetadataValue(Dictionary<string, string>? metadata, string key) =>
        metadata is not null && metadata.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>Resolve a plan id from the subscription's Stripe price, falling
    /// back to the tenant/plan metadata. Returns null if it is not a known plan.</summary>
    internal static string? ResolvePlan(string? priceId, IReadOnlyDictionary<string, string> priceToPlan, string? metadataPlanId)
    {
        if (priceId is not null && priceToPlan.TryGetValue(priceId, out var mapped) && Plans.IsKnown(mapped))
        {
            return mapped;
        }
        return metadataPlanId is not null && Plans.IsKnown(metadataPlanId) ? metadataPlanId : null;
    }

    /// <summary>Normalize a Stripe subscription status into our entitlement
    /// vocabulary. A delete event is always a cancellation; incomplete/unpaid
    /// states are treated as not-entitled (canceled) rather than granting access.</summary>
    internal static string MapStatus(string eventType, string stripeStatus)
    {
        if (eventType == EventTypes.CustomerSubscriptionDeleted)
        {
            return SubscriptionStatus.Canceled;
        }
        return stripeStatus switch
        {
            "trialing" => SubscriptionStatus.Trialing,
            "active" => SubscriptionStatus.Active,
            "past_due" => SubscriptionStatus.PastDue,
            _ => SubscriptionStatus.Canceled, // canceled, unpaid, incomplete(_expired), paused
        };
    }

    /// <summary>The current period end. Stripe moved this onto subscription items;
    /// take the earliest item's period end, defaulting to null when absent.</summary>
    private static DateTimeOffset? PeriodEndOf(Subscription sub)
    {
        var items = sub.Items?.Data;
        if (items is null || items.Count == 0)
        {
            return null;
        }
        DateTimeOffset? earliest = null;
        foreach (var item in items)
        {
            var end = new DateTimeOffset(item.CurrentPeriodEnd, TimeSpan.Zero);
            if (earliest is null || end < earliest)
            {
                earliest = end;
            }
        }
        return earliest;
    }
}
