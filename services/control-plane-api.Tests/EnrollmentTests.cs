using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ControlPlane.Api.Tests;

/// <summary>Hosts the API with a known admin token for tests.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:AdminToken"] = "test-admin",
            }));
    }
}

public sealed class EnrollmentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public EnrollmentTests(ApiFactory factory) => _factory = factory;

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return client;
    }

    private sealed record TenantDto(string Id, string Name);
    private sealed record BootstrapDto(string Token, DateTimeOffset ExpiresAt);
    private sealed record EnrollDto(string GatewayId, string TenantId, string DeviceCredential);
    private sealed record GatewayDto(string Id, string TenantId, string Name);

    private static async Task<string> CreateTenant(HttpClient admin, string name)
    {
        var res = await admin.PostAsJsonAsync("/api/tenants", new { name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var tenant = await res.Content.ReadFromJsonAsync<TenantDto>();
        return tenant!.Id;
    }

    [Fact]
    public async Task Management_Requires_Admin_Token()
    {
        var anon = _factory.CreateClient();
        var res = await anon.PostAsJsonAsync("/api/tenants", new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Gateway_Enrolls_And_Appears_In_Its_Tenant_Only()
    {
        var admin = AdminClient();
        var tenantA = await CreateTenant(admin, "Lab A");
        var tenantB = await CreateTenant(admin, "Lab B");

        // Issue a bootstrap token for tenant A.
        var tokRes = await admin.PostAsync($"/api/tenants/{tenantA}/enrollment-tokens", null);
        Assert.Equal(HttpStatusCode.OK, tokRes.StatusCode);
        var boot = await tokRes.Content.ReadFromJsonAsync<BootstrapDto>();

        // Gateway redeems the bootstrap token (no admin auth needed).
        var anon = _factory.CreateClient();
        var enrollRes = await anon.PostAsJsonAsync("/api/gateways/enroll",
            new { bootstrapToken = boot!.Token, name = "edge-1" });
        Assert.Equal(HttpStatusCode.OK, enrollRes.StatusCode);
        var enrolled = await enrollRes.Content.ReadFromJsonAsync<EnrollDto>();
        Assert.Equal(tenantA, enrolled!.TenantId);
        Assert.False(string.IsNullOrEmpty(enrolled.DeviceCredential));

        // Tenant A sees the gateway; tenant B does not (tenant isolation).
        var aGateways = await admin.GetFromJsonAsync<List<GatewayDto>>($"/api/tenants/{tenantA}/gateways");
        var bGateways = await admin.GetFromJsonAsync<List<GatewayDto>>($"/api/tenants/{tenantB}/gateways");
        Assert.Single(aGateways!);
        Assert.Equal(enrolled.GatewayId, aGateways![0].Id);
        Assert.Empty(bGateways!);
    }

    [Fact]
    public async Task Bootstrap_Token_Is_Single_Use()
    {
        var admin = AdminClient();
        var tenant = await CreateTenant(admin, "Lab C");
        var tokRes = await admin.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null);
        var boot = await tokRes.Content.ReadFromJsonAsync<BootstrapDto>();

        var anon = _factory.CreateClient();
        var first = await anon.PostAsJsonAsync("/api/gateways/enroll", new { bootstrapToken = boot!.Token });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Re-using the same token is rejected.
        var second = await anon.PostAsJsonAsync("/api/gateways/enroll", new { bootstrapToken = boot.Token });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Invalid_Bootstrap_Token_Is_Unauthorized()
    {
        var anon = _factory.CreateClient();
        var res = await anon.PostAsJsonAsync("/api/gateways/enroll", new { bootstrapToken = "nonsense" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Enrollment_Token_For_Unknown_Tenant_Is_NotFound()
    {
        var admin = AdminClient();
        var res = await admin.PostAsync("/api/tenants/ten_does_not_exist/enrollment-tokens", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
