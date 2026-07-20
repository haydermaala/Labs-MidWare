using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>Gateway telemetry ingest (PHI-free counts) and its surfacing in the
/// fleet view. Telemetry is authenticated by the device credential and also
/// serves as a heartbeat.</summary>
public sealed class TelemetryTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public TelemetryTests(ApiFactory factory) => _factory = factory;

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

    private sealed record TenantDto(string Id);
    private sealed record BootstrapDto(string Token);
    private sealed record EnrollDto(string GatewayId, string TenantId, string DeviceCredential);
    private sealed record TelemetryDto(long Captured, long Pending, long Delivered, long Dead, DateTimeOffset? LastCaptureAt);
    private sealed record GatewayDto(string Id, string Status, DateTimeOffset? LastSeenAt, TelemetryDto Telemetry);

    private async Task<EnrollDto> EnrollGateway()
    {
        var admin = AdminClient();
        var tenant = (await (await admin.PostAsJsonAsync("/api/tenants", new { name = "Telemetry Lab" }))
            .Content.ReadFromJsonAsync<TenantDto>())!;
        var boot = (await (await admin.PostAsync($"/api/tenants/{tenant.Id}/enrollment-tokens", null))
            .Content.ReadFromJsonAsync<BootstrapDto>())!;
        return (await (await _factory.CreateClient().PostAsJsonAsync("/api/gateways/enroll",
            new { bootstrapToken = boot.Token, name = "edge-telemetry" }))
            .Content.ReadFromJsonAsync<EnrollDto>())!;
    }

    private async Task<GatewayDto> GatewayView(string tenantId, string gatewayId)
    {
        var gateways = await AdminClient().GetFromJsonAsync<List<GatewayDto>>($"/api/tenants/{tenantId}/gateways");
        return gateways!.Single(g => g.Id == gatewayId);
    }

    [Fact]
    public async Task Telemetry_Requires_A_Valid_Device_Credential()
    {
        var gw = await EnrollGateway();
        var body = new { captured = 1, pending = 1, delivered = 0, dead = 0, lastCaptureAt = (DateTimeOffset?)null };

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().PostAsJsonAsync("/api/gateways/telemetry", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await DeviceClient(gw.GatewayId, "wrong").PostAsJsonAsync("/api/gateways/telemetry", body)).StatusCode);
    }

    [Fact]
    public async Task Telemetry_Updates_Counts_And_Marks_The_Gateway_Online()
    {
        var gw = await EnrollGateway();
        var before = await GatewayView(gw.TenantId, gw.GatewayId);
        Assert.Equal("never", before.Status);
        Assert.Equal(0, before.Telemetry.Captured);

        var at = DateTimeOffset.UtcNow;
        var device = DeviceClient(gw.GatewayId, gw.DeviceCredential);
        var res = await device.PostAsJsonAsync("/api/gateways/telemetry",
            new { captured = 7, pending = 2, delivered = 5, dead = 0, lastCaptureAt = at });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var after = await GatewayView(gw.TenantId, gw.GatewayId);
        Assert.Equal("online", after.Status); // a telemetry report is also a heartbeat
        Assert.NotNull(after.LastSeenAt);
        Assert.Equal(7, after.Telemetry.Captured);
        Assert.Equal(2, after.Telemetry.Pending);
        Assert.Equal(5, after.Telemetry.Delivered);
        Assert.Equal(0, after.Telemetry.Dead);
        Assert.NotNull(after.Telemetry.LastCaptureAt);
    }

    [Fact]
    public async Task Telemetry_Clamps_Negative_Counts_To_Zero()
    {
        var gw = await EnrollGateway();
        var device = DeviceClient(gw.GatewayId, gw.DeviceCredential);
        await device.PostAsJsonAsync("/api/gateways/telemetry",
            new { captured = -5, pending = -1, delivered = 3, dead = -2, lastCaptureAt = (DateTimeOffset?)null });

        var view = await GatewayView(gw.TenantId, gw.GatewayId);
        Assert.Equal(0, view.Telemetry.Captured);
        Assert.Equal(0, view.Telemetry.Pending);
        Assert.Equal(3, view.Telemetry.Delivered);
        Assert.Equal(0, view.Telemetry.Dead);
    }
}
