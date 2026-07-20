using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ControlPlane.Api.Tests;

/// <summary>Plan catalog, entitlements, and server-side quota enforcement (E1).</summary>
public sealed class BillingTests : IClassFixture<AuthApiFactory>
{
    private readonly AuthApiFactory _factory;

    public BillingTests(AuthApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id);
    private sealed record UserDto(string Id);
    private sealed record LoginDto(string SessionToken);
    private sealed record EntitlementsDto(string PlanId, string PlanName, string Status, int GatewayQuota, string[] Features);
    private sealed record BillingDto(EntitlementsDto Entitlements);
    private sealed record PlanDto(string Id, string Name, int GatewayQuota, string[] Features);

    private HttpClient Admin()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return c;
    }

    private HttpClient Session(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<string> NewTenant(string name) =>
        (await (await Admin().PostAsJsonAsync("/api/tenants", new { name })).Content.ReadFromJsonAsync<TenantDto>())!.Id;

    private async Task<string> NewOwner(string email, string tenantId)
    {
        var user = (await (await Admin().PostAsJsonAsync("/api/admin/users",
            new { email, password = "correct horse battery staple" })).Content.ReadFromJsonAsync<UserDto>())!;
        await Admin().PostAsJsonAsync("/api/admin/memberships", new { userId = user.Id, tenantId, role = "owner" });
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        return (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken;
    }

    [Fact]
    public async Task Plan_Catalog_Is_Public_And_Publishes_No_Prices()
    {
        var plans = await _factory.CreateClient().GetFromJsonAsync<List<PlanDto>>("/api/billing/plans");
        Assert.Contains(plans!, p => p.Id == "trial");
        Assert.Contains(plans!, p => p.Id == "network" && p.GatewayQuota == -1);
        // The catalog is entitlement scope only — no monetary field is exposed.
        var raw = await _factory.CreateClient().GetStringAsync("/api/billing/plans");
        Assert.DoesNotContain("price", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_New_Tenant_Defaults_To_The_Trial_Plan()
    {
        var tenant = await NewTenant("Fresh Lab");
        var session = await NewOwner("bill-fresh@example.test", tenant);
        var billing = await Session(session).GetFromJsonAsync<BillingDto>($"/api/tenants/{tenant}/billing");
        Assert.Equal("trial", billing!.Entitlements.PlanId);
        Assert.Equal(2, billing.Entitlements.GatewayQuota);
        Assert.Equal("trialing", billing.Entitlements.Status);
    }

    [Fact]
    public async Task Gateway_Enrollment_Is_Blocked_When_The_Trial_Quota_Is_Reached()
    {
        var tenant = await NewTenant("Quota Lab");
        var owner = Session(await NewOwner("bill-quota@example.test", tenant));

        // Trial quota is 2: the first two enrollment tokens issue…
        for (var i = 0; i < 2; i++)
        {
            var boot = await owner.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null);
            Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
            var token = (await boot.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["token"].ToString();
            var enroll = await _factory.CreateClient().PostAsJsonAsync("/api/gateways/enroll",
                new { bootstrapToken = token, name = $"edge-{i}" });
            Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        }

        // …and the third is refused with 402 (quota reached), server-side.
        var blocked = await owner.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null);
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
    }

    [Fact]
    public async Task Upgrading_The_Plan_Raises_The_Quota()
    {
        var tenant = await NewTenant("Upgrade Lab");
        var owner = Session(await NewOwner("bill-upgrade@example.test", tenant));

        // Simulate a provider event moving the tenant onto Laboratory (quota 25)
        // via the fake webhook — exercised end-to-end in E2; here we just confirm
        // the entitlement recomputes and the quota block lifts.
        // Fill the trial quota first.
        for (var i = 0; i < 2; i++)
        {
            var boot = await owner.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null);
            var token = (await boot.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["token"].ToString();
            await _factory.CreateClient().PostAsJsonAsync("/api/gateways/enroll", new { bootstrapToken = token, name = $"e{i}" });
        }
        Assert.Equal(HttpStatusCode.PaymentRequired,
            (await owner.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);

        _factory.Services.GetRequiredService<BillingService>()
            .UpsertSubscription(tenant, "laboratory", "active", "cus_1", "sub_1", DateTimeOffset.UtcNow.AddDays(30), false);

        Assert.Equal(HttpStatusCode.OK,
            (await owner.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);
        var billing = await owner.GetFromJsonAsync<BillingDto>($"/api/tenants/{tenant}/billing");
        Assert.Equal("laboratory", billing!.Entitlements.PlanId);
        Assert.Contains("bidirectional", billing.Entitlements.Features);
    }

    [Fact]
    public async Task A_Canceled_Subscription_Falls_Back_To_Trial_Entitlements()
    {
        var tenant = await NewTenant("Cancel Lab");
        var owner = Session(await NewOwner("bill-cancel@example.test", tenant));

        _factory.Services.GetRequiredService<BillingService>()
            .UpsertSubscription(tenant, "network", "canceled", "cus_2", "sub_2", null, false);

        var billing = await owner.GetFromJsonAsync<BillingDto>($"/api/tenants/{tenant}/billing");
        // Entitlement plan drops to trial even though the record names network.
        Assert.Equal("trial", billing!.Entitlements.PlanId);
        Assert.Equal("canceled", billing.Entitlements.Status);
    }

    [Fact]
    public async Task Billing_Read_Requires_Tenant_Membership()
    {
        var tenant = await NewTenant("Private Billing Lab");
        var outsider = Session(await NewOwner("bill-outsider@example.test", await NewTenant("Elsewhere")));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await outsider.GetAsync($"/api/tenants/{tenant}/billing")).StatusCode);
    }

    // --- E2: checkout, portal, webhooks ------------------------------------

    private sealed record UrlDto(string Url);
    private sealed record AppliedDto(bool Applied);

    /// <summary>POST a raw webhook envelope with the shared-secret signature header.</summary>
    private async Task<HttpResponseMessage> PostWebhook(string json, string? signature = "fake-signature")
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/billing/webhook")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (signature is not null) msg.Headers.Add("X-Billing-Signature", signature);
        return await _factory.CreateClient().SendAsync(msg);
    }

    [Fact]
    public async Task Checkout_Returns_A_Provider_Redirect_For_A_Billing_Manager()
    {
        var tenant = await NewTenant("Checkout Lab");
        var owner = Session(await NewOwner("bill-checkout@example.test", tenant));
        var res = await owner.PostAsJsonAsync($"/api/tenants/{tenant}/billing/checkout", new { planId = "laboratory" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<UrlDto>();
        Assert.Contains("plan=laboratory", body!.Url);
        Assert.Contains($"tenant={tenant}", body.Url);
    }

    [Fact]
    public async Task Checkout_Rejects_The_Trial_And_Unknown_Plans()
    {
        var tenant = await NewTenant("Bad Checkout Lab");
        var owner = Session(await NewOwner("bill-badcheckout@example.test", tenant));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/billing/checkout", new { planId = "trial" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/billing/checkout", new { planId = "nope" })).StatusCode);
    }

    [Fact]
    public async Task Checkout_Requires_Billing_Manager_Authorization()
    {
        var tenant = await NewTenant("Guarded Checkout Lab");
        var outsider = Session(await NewOwner("bill-guard@example.test", await NewTenant("Elsewhere 2")));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await outsider.PostAsJsonAsync($"/api/tenants/{tenant}/billing/checkout", new { planId = "laboratory" })).StatusCode);
    }

    [Fact]
    public async Task Portal_Returns_A_Provider_Redirect_For_A_Billing_Manager()
    {
        var tenant = await NewTenant("Portal Lab");
        var owner = Session(await NewOwner("bill-portal@example.test", tenant));
        var res = await owner.PostAsync($"/api/tenants/{tenant}/billing/portal", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("portal=fake", (await res.Content.ReadFromJsonAsync<UrlDto>())!.Url);
    }

    [Fact]
    public async Task Webhook_Rejects_A_Bad_Signature()
    {
        var tenant = await NewTenant("Webhook Sig Lab");
        var json = $$"""{"eventId":"evt_sig_1","tenantId":"{{tenant}}","planId":"pilot","status":"active"}""";
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhook(json, "wrong-secret")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostWebhook(json, signature: null)).StatusCode);
        // …and no subscription was created.
        var owner = Session(await NewOwner("bill-sig@example.test", tenant));
        var billing = await owner.GetFromJsonAsync<BillingDto>($"/api/tenants/{tenant}/billing");
        Assert.Equal("trial", billing!.Entitlements.PlanId);
    }

    [Fact]
    public async Task Webhook_Applies_A_Subscription_And_Is_Idempotent_On_Replay()
    {
        var tenant = await NewTenant("Webhook Apply Lab");
        var owner = Session(await NewOwner("bill-apply@example.test", tenant));
        var json = $$"""
            {"eventId":"evt_apply_1","tenantId":"{{tenant}}","planId":"laboratory","status":"active",
             "customerId":"cus_wh","subscriptionId":"sub_wh"}
            """;

        // First delivery applies the change.
        var first = await PostWebhook(json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<AppliedDto>())!.Applied);

        var billing = await owner.GetFromJsonAsync<BillingDto>($"/api/tenants/{tenant}/billing");
        Assert.Equal("laboratory", billing!.Entitlements.PlanId);
        Assert.Equal("active", billing.Entitlements.Status);

        // Replaying the same event id is a no-op (accepted, not applied).
        var replay = await PostWebhook(json);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False((await replay.Content.ReadFromJsonAsync<AppliedDto>())!.Applied);
    }

    [Fact]
    public async Task Webhook_Can_Cancel_A_Subscription_Dropping_Entitlements_To_Trial()
    {
        var tenant = await NewTenant("Webhook Cancel Lab");
        var owner = Session(await NewOwner("bill-whcancel@example.test", tenant));

        await PostWebhook($$"""{"eventId":"evt_c_1","tenantId":"{{tenant}}","planId":"network","status":"active"}""");
        Assert.Equal("network",
            (await owner.GetFromJsonAsync<BillingDto>($"/api/tenants/{tenant}/billing"))!.Entitlements.PlanId);

        await PostWebhook($$"""{"eventId":"evt_c_2","tenantId":"{{tenant}}","planId":"network","status":"canceled"}""");
        var after = await owner.GetFromJsonAsync<BillingDto>($"/api/tenants/{tenant}/billing");
        Assert.Equal("trial", after!.Entitlements.PlanId);
        Assert.Equal("canceled", after.Entitlements.Status);
    }
}
