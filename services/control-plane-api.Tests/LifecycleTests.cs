using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>HTTP surface for tenant/gateway lifecycle (admin-only, tenant-scoped).</summary>
public sealed class LifecycleTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LifecycleTests(ApiFactory factory) => _factory = factory;

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return client;
    }

    private sealed record TenantDto(string Id, string Name, bool Active);
    private sealed record BootstrapDto(string Token, DateTimeOffset ExpiresAt);
    private sealed record EnrollDto(string GatewayId, string TenantId, string DeviceCredential);
    private sealed record GatewayDto(string Id, string TenantId, string Name, bool Active);

    private static async Task<string> CreateTenant(HttpClient admin, string name)
    {
        var res = await admin.PostAsJsonAsync("/api/tenants", new { name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<TenantDto>())!.Id;
    }

    [Fact]
    public async Task Lifecycle_Endpoints_Require_Admin_Token()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/tenants/ten_x/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/tenants/ten_x/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsync("/api/tenants/ten_x/gateways/gw_x/decommission", null)).StatusCode);
    }

    [Fact]
    public async Task Deactivate_Blocks_Enrollment_Tokens_Then_Reactivate_Restores()
    {
        var admin = AdminClient();
        var tenant = await CreateTenant(admin, "Lab A");

        var deactivate = await admin.PostAsync($"/api/tenants/{tenant}/deactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        // No token can be issued while deactivated.
        var blocked = await admin.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null);
        Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);

        // The tenant is reported inactive.
        var tenants = await admin.GetFromJsonAsync<List<TenantDto>>("/api/tenants");
        Assert.False(tenants!.Single(t => t.Id == tenant).Active);

        var reactivate = await admin.PostAsync($"/api/tenants/{tenant}/reactivate", null);
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);

        var restored = await admin.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task Deactivate_Unknown_Tenant_Is_NotFound()
    {
        var admin = AdminClient();
        var res = await admin.PostAsync("/api/tenants/ten_missing/deactivate", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Decommission_Revokes_Credential_And_Marks_Gateway_Inactive()
    {
        var admin = AdminClient();
        var anon = _factory.CreateClient();
        var tenant = await CreateTenant(admin, "Lab A");

        var boot = await (await admin.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null))
            .Content.ReadFromJsonAsync<BootstrapDto>();
        var enrolled = await (await anon.PostAsJsonAsync("/api/gateways/enroll",
                new { bootstrapToken = boot!.Token, name = "edge-1" }))
            .Content.ReadFromJsonAsync<EnrollDto>();

        // The gateway can fetch its config before decommissioning.
        var before = _factory.CreateClient();
        before.DefaultRequestHeaders.Add("X-Gateway-Id", enrolled!.GatewayId);
        before.DefaultRequestHeaders.Add("X-Gateway-Credential", enrolled.DeviceCredential);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await before.GetAsync("/api/gateways/config")).StatusCode);

        // Decommission it.
        var res = await admin.PostAsync($"/api/tenants/{tenant}/gateways/{enrolled.GatewayId}/decommission", null);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        // Credential is revoked: the gateway can no longer authenticate.
        var after = _factory.CreateClient();
        after.DefaultRequestHeaders.Add("X-Gateway-Id", enrolled.GatewayId);
        after.DefaultRequestHeaders.Add("X-Gateway-Credential", enrolled.DeviceCredential);
        Assert.Equal(HttpStatusCode.Unauthorized, (await after.GetAsync("/api/gateways/config")).StatusCode);

        // It is still listed for the tenant, marked inactive (retained for audit).
        var gateways = await admin.GetFromJsonAsync<List<GatewayDto>>($"/api/tenants/{tenant}/gateways");
        Assert.False(gateways!.Single(g => g.Id == enrolled.GatewayId).Active);
    }

    [Fact]
    public async Task Decommission_Is_Tenant_Scoped()
    {
        var admin = AdminClient();
        var anon = _factory.CreateClient();
        var tenantA = await CreateTenant(admin, "Lab A");
        var tenantB = await CreateTenant(admin, "Lab B");

        var boot = await (await admin.PostAsync($"/api/tenants/{tenantA}/enrollment-tokens", null))
            .Content.ReadFromJsonAsync<BootstrapDto>();
        var enrolled = await (await anon.PostAsJsonAsync("/api/gateways/enroll",
                new { bootstrapToken = boot!.Token, name = "edge-1" }))
            .Content.ReadFromJsonAsync<EnrollDto>();

        // Tenant B cannot decommission tenant A's gateway.
        var res = await admin.PostAsync($"/api/tenants/{tenantB}/gateways/{enrolled!.GatewayId}/decommission", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
