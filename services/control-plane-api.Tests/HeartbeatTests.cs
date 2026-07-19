using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>HTTP surface for gateway heartbeat + derived liveness status.</summary>
public sealed class HeartbeatTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public HeartbeatTests(ApiFactory factory) => _factory = factory;

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return client;
    }

    private HttpClient DeviceClient(string gatewayId, string credential)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Gateway-Id", gatewayId);
        client.DefaultRequestHeaders.Add("X-Gateway-Credential", credential);
        return client;
    }

    private sealed record TenantDto(string Id, string Name, bool Active);
    private sealed record BootstrapDto(string Token, DateTimeOffset ExpiresAt);
    private sealed record EnrollDto(string GatewayId, string TenantId, string DeviceCredential);
    private sealed record GatewayDto(string Id, string Name, bool Active, DateTimeOffset? LastSeenAt, string Status);

    private async Task<(string tenantId, EnrollDto gateway)> EnrollGateway()
    {
        var admin = AdminClient();
        var res = await admin.PostAsJsonAsync("/api/tenants", new { name = "Lab A" });
        var tenant = (await res.Content.ReadFromJsonAsync<TenantDto>())!;
        var boot = await (await admin.PostAsync($"/api/tenants/{tenant.Id}/enrollment-tokens", null))
            .Content.ReadFromJsonAsync<BootstrapDto>();
        var anon = _factory.CreateClient();
        var enrolled = await (await anon.PostAsJsonAsync("/api/gateways/enroll",
                new { bootstrapToken = boot!.Token, name = "edge-1" }))
            .Content.ReadFromJsonAsync<EnrollDto>();
        return (tenant.Id, enrolled!);
    }

    private static async Task<GatewayDto> GatewayView(HttpClient admin, string tenantId, string gatewayId)
    {
        var gateways = await admin.GetFromJsonAsync<List<GatewayDto>>($"/api/tenants/{tenantId}/gateways");
        return gateways!.Single(g => g.Id == gatewayId);
    }

    [Fact]
    public async Task Heartbeat_Requires_A_Valid_Device_Credential()
    {
        var (_, gw) = await EnrollGateway();

        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/gateways/heartbeat", null)).StatusCode);

        var wrong = DeviceClient(gw.GatewayId, "not-the-credential");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsync("/api/gateways/heartbeat", null)).StatusCode);
    }

    [Fact]
    public async Task Heartbeat_Marks_Gateway_Online_With_LastSeen()
    {
        var admin = AdminClient();
        var (tenantId, gw) = await EnrollGateway();

        // Never seen before its first heartbeat.
        var before = await GatewayView(admin, tenantId, gw.GatewayId);
        Assert.Equal("never", before.Status);
        Assert.Null(before.LastSeenAt);

        var device = DeviceClient(gw.GatewayId, gw.DeviceCredential);
        Assert.Equal(HttpStatusCode.NoContent, (await device.PostAsync("/api/gateways/heartbeat", null)).StatusCode);

        var after = await GatewayView(admin, tenantId, gw.GatewayId);
        Assert.Equal("online", after.Status);
        Assert.NotNull(after.LastSeenAt);
    }

    [Fact]
    public async Task Decommissioned_Gateway_Cannot_Heartbeat()
    {
        var admin = AdminClient();
        var (tenantId, gw) = await EnrollGateway();

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/tenants/{tenantId}/gateways/{gw.GatewayId}/decommission", null)).StatusCode);

        var device = DeviceClient(gw.GatewayId, gw.DeviceCredential);
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.PostAsync("/api/gateways/heartbeat", null)).StatusCode);

        // And it reports the decommissioned status in the fleet view.
        Assert.Equal("decommissioned", (await GatewayView(admin, tenantId, gw.GatewayId)).Status);
    }
}
