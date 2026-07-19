using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

public sealed class ConfigAndAuditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ConfigAndAuditTests(ApiFactory factory) => _factory = factory;

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return client;
    }

    private sealed record TenantDto(string Id);
    private sealed record BootstrapDto(string Token);
    private sealed record EnrollDto(string GatewayId, string TenantId, string DeviceCredential);
    private sealed record ConfigDto(string GatewayId, int Version, string Environment, string SettingsJson);
    private sealed record AuditDto(string Kind, string TenantId, string Detail);

    private async Task<(string tenantId, EnrollDto enrolled)> EnrollGateway(HttpClient admin)
    {
        var tRes = await admin.PostAsJsonAsync("/api/tenants", new { name = "Lab" });
        var tenant = await tRes.Content.ReadFromJsonAsync<TenantDto>();
        var tokRes = await admin.PostAsync($"/api/tenants/{tenant!.Id}/enrollment-tokens", null);
        var boot = await tokRes.Content.ReadFromJsonAsync<BootstrapDto>();
        var anon = _factory.CreateClient();
        var enrollRes = await anon.PostAsJsonAsync("/api/gateways/enroll",
            new { bootstrapToken = boot!.Token, name = "edge-1" });
        var enrolled = await enrollRes.Content.ReadFromJsonAsync<EnrollDto>();
        return (tenant.Id, enrolled!);
    }

    [Fact]
    public async Task Gateway_Fetches_Published_Config_With_Its_Credential()
    {
        var admin = AdminClient();
        var (tenantId, enrolled) = await EnrollGateway(admin);

        // Admin publishes config.
        var pubRes = await admin.PostAsJsonAsync(
            $"/api/tenants/{tenantId}/gateways/{enrolled.GatewayId}/config",
            new { pollIntervalSeconds = 30, mode = "passive_capture" });
        Assert.Equal(HttpStatusCode.OK, pubRes.StatusCode);
        var published = await pubRes.Content.ReadFromJsonAsync<ConfigDto>();
        Assert.Equal(1, published!.Version);
        Assert.Equal("non-production", published.Environment);

        // Gateway fetches its config with its device credential.
        var gw = _factory.CreateClient();
        gw.DefaultRequestHeaders.Add("X-Gateway-Id", enrolled.GatewayId);
        gw.DefaultRequestHeaders.Add("X-Gateway-Credential", enrolled.DeviceCredential);
        var cfg = await gw.GetFromJsonAsync<ConfigDto>("/api/gateways/config");
        Assert.Equal(enrolled.GatewayId, cfg!.GatewayId);
        Assert.Contains("passive_capture", cfg.SettingsJson);
    }

    [Fact]
    public async Task Gateway_Config_Requires_Valid_Credential()
    {
        var admin = AdminClient();
        var (_, enrolled) = await EnrollGateway(admin);

        var gw = _factory.CreateClient();
        gw.DefaultRequestHeaders.Add("X-Gateway-Id", enrolled.GatewayId);
        gw.DefaultRequestHeaders.Add("X-Gateway-Credential", "wrong-credential");
        var res = await gw.GetAsync("/api/gateways/config");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Publishing_Config_To_Wrong_Tenant_Is_NotFound()
    {
        var admin = AdminClient();
        var (_, enrolled) = await EnrollGateway(admin);

        // A different tenant cannot publish to this gateway.
        var otherRes = await admin.PostAsJsonAsync("/api/tenants", new { name = "Other" });
        var other = await otherRes.Content.ReadFromJsonAsync<TenantDto>();
        var pub = await admin.PostAsJsonAsync(
            $"/api/tenants/{other!.Id}/gateways/{enrolled.GatewayId}/config",
            new { x = 1 });
        Assert.Equal(HttpStatusCode.NotFound, pub.StatusCode);
    }

    [Fact]
    public async Task Audit_Log_Records_Lifecycle_Events()
    {
        var admin = AdminClient();
        var (tenantId, enrolled) = await EnrollGateway(admin);
        await admin.PostAsJsonAsync(
            $"/api/tenants/{tenantId}/gateways/{enrolled.GatewayId}/config", new { x = 1 });

        var audit = await admin.GetFromJsonAsync<List<AuditDto>>($"/api/tenants/{tenantId}/audit");
        var kinds = audit!.Select(a => a.Kind).ToList();
        Assert.Contains("tenant.created", kinds);
        Assert.Contains("enrollment.token_issued", kinds);
        Assert.Contains("gateway.enrolled", kinds);
        Assert.Contains("config.published", kinds);
    }
}
