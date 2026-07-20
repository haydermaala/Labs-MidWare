// Payment provider seam. IBillingProvider is the only place that talks to an
// external billing system; the rest of the app depends on this interface.
//
// FakeBillingProvider backs local dev, tests, and any environment without
// provider keys: it fabricates checkout/portal URLs and lets tests simulate the
// lifecycle deterministically. A Stripe adapter is added behind this interface
// when test-mode keys are configured (Phase E2). No card data ever crosses here.

namespace ControlPlane.Api;

/// <summary>A hosted checkout or billing-portal redirect.</summary>
public sealed record ProviderRedirect(string Url);

/// <summary>A normalized subscription-change event parsed from a provider webhook.</summary>
public sealed record ProviderSubscriptionEvent(
    string EventId,
    string TenantId,
    string PlanId,
    string Status,
    string? CustomerId,
    string? SubscriptionId,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd);

/// <summary>Provider-agnostic billing operations.</summary>
public interface IBillingProvider
{
    /// <summary>Provider identifier for diagnostics (e.g. "fake", "stripe").</summary>
    string Name { get; }

    /// <summary>Start hosted checkout for a plan; returns the redirect URL.</summary>
    Task<ProviderRedirect> CreateCheckoutAsync(string tenantId, string planId, CancellationToken ct = default);

    /// <summary>Open the provider's billing portal for an existing customer.</summary>
    Task<ProviderRedirect> CreatePortalAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Verify a webhook's signature and parse it into a normalized event, or null
    /// if the signature/payload is not valid. Implementations enforce replay and
    /// timestamp tolerance internally.
    /// </summary>
    ProviderSubscriptionEvent? ParseWebhook(string payload, string? signatureHeader);
}

/// <summary>
/// Deterministic provider for dev/tests. Checkout/portal return app URLs; the
/// "webhook" is a plain JSON envelope the tests post to exercise the lifecycle.
/// </summary>
public sealed class FakeBillingProvider : IBillingProvider
{
    private readonly IConfiguration _config;
    public FakeBillingProvider(IConfiguration config) => _config = config;

    public string Name => "fake";

    private string BaseUrl => (_config["ControlPlane:PublicBaseUrl"] ?? "http://localhost:5173").TrimEnd('/');

    public Task<ProviderRedirect> CreateCheckoutAsync(string tenantId, string planId, CancellationToken ct = default) =>
        Task.FromResult(new ProviderRedirect(
            $"{BaseUrl}/billing/checkout-complete?plan={Uri.EscapeDataString(planId)}&tenant={Uri.EscapeDataString(tenantId)}"));

    public Task<ProviderRedirect> CreatePortalAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(new ProviderRedirect($"{BaseUrl}/billing?portal=fake&tenant={Uri.EscapeDataString(tenantId)}"));

    /// <summary>
    /// Accepts a JSON envelope: { eventId, tenantId, planId, status, customerId?,
    /// subscriptionId?, currentPeriodEnd?, cancelAtPeriodEnd? }. The signature
    /// header must equal the configured shared secret (default "fake-signature"),
    /// so the endpoint still exercises real signature-gating in dev/tests.
    /// </summary>
    public ProviderSubscriptionEvent? ParseWebhook(string payload, string? signatureHeader)
    {
        var expected = _config["Billing:FakeWebhookSecret"] ?? "fake-signature";
        if (!string.Equals(signatureHeader, expected, StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            var eventId = S("eventId");
            var tenantId = S("tenantId");
            var planId = S("planId");
            var status = S("status");
            if (eventId is null || tenantId is null || planId is null || status is null || !Plans.IsKnown(planId))
            {
                return null;
            }
            DateTimeOffset? periodEnd = root.TryGetProperty("currentPeriodEnd", out var pe) && pe.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTimeOffset.TryParse(pe.GetString(), out var parsed) ? parsed : null;
            var cancel = root.TryGetProperty("cancelAtPeriodEnd", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True;
            return new ProviderSubscriptionEvent(eventId, tenantId, planId, status, S("customerId"), S("subscriptionId"), periodEnd, cancel);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
