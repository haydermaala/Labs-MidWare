namespace ControlPlane.Api.Tests;

/// <summary>
/// Unit coverage for the Stripe adapter's pure mapping logic — the parts that do
/// not require a live Stripe call. Checkout/portal creation and signature
/// verification are exercised against Stripe test-mode in a live check once keys
/// are configured; here we pin the entitlement-affecting translation.
/// </summary>
public sealed class StripeMappingTests
{
    private static readonly Dictionary<string, string> PriceToPlan = new(StringComparer.Ordinal)
    {
        ["price_pilot"] = Plans.Pilot,
        ["price_lab"] = Plans.Laboratory,
        ["price_net"] = Plans.Network,
    };

    [Theory]
    [InlineData("customer.subscription.updated", "active", "active")]
    [InlineData("customer.subscription.updated", "trialing", "trialing")]
    [InlineData("customer.subscription.updated", "past_due", "past_due")]
    // A delete event is always a cancellation, whatever the object's status says.
    [InlineData("customer.subscription.deleted", "active", "canceled")]
    // Not-yet-paid / failed states must never grant entitlements.
    [InlineData("customer.subscription.updated", "incomplete", "canceled")]
    [InlineData("customer.subscription.updated", "incomplete_expired", "canceled")]
    [InlineData("customer.subscription.updated", "unpaid", "canceled")]
    [InlineData("customer.subscription.updated", "paused", "canceled")]
    public void MapStatus_Normalizes_Into_Our_Vocabulary(string eventType, string stripeStatus, string expected)
    {
        Assert.Equal(expected, StripeBillingProvider.MapStatus(eventType, stripeStatus));
    }

    [Fact]
    public void ResolvePlan_Prefers_The_Mapped_Price()
    {
        Assert.Equal(Plans.Laboratory, StripeBillingProvider.ResolvePlan("price_lab", PriceToPlan, metadataPlanId: null));
    }

    [Fact]
    public void ResolvePlan_Falls_Back_To_Metadata_When_The_Price_Is_Unknown()
    {
        Assert.Equal(Plans.Network, StripeBillingProvider.ResolvePlan("price_unknown", PriceToPlan, Plans.Network));
    }

    [Fact]
    public void ResolvePlan_Returns_Null_For_An_Unrecognizable_Plan()
    {
        Assert.Null(StripeBillingProvider.ResolvePlan("price_unknown", PriceToPlan, metadataPlanId: null));
        Assert.Null(StripeBillingProvider.ResolvePlan("price_unknown", PriceToPlan, "not-a-plan"));
    }
}
