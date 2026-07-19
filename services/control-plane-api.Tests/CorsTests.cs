using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ControlPlane.Api.Tests;

/// <summary>Hosts the API with a known admin token and an allowlisted console origin.</summary>
public sealed class CorsApiFactory : WebApplicationFactory<Program>
{
    public const string AllowedOrigin = "https://console.example";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:AdminToken"] = "test-admin",
                ["ControlPlane:AllowedOrigins"] = AllowedOrigin,
            }));
    }
}

public sealed class CorsTests : IClassFixture<CorsApiFactory>
{
    private readonly CorsApiFactory _factory;

    public CorsTests(CorsApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Allowlisted_Origin_Gets_Cors_Header()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/tenants");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        req.Headers.Add("Origin", CorsApiFactory.AllowedOrigin);

        var res = await client.SendAsync(req);

        Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(CorsApiFactory.AllowedOrigin, res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Preflight_From_Allowlisted_Origin_Permits_Post()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Options, "/api/tenants/ten_x/deactivate");
        req.Headers.Add("Origin", CorsApiFactory.AllowedOrigin);
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "authorization");

        var res = await client.SendAsync(req);

        Assert.Equal(CorsApiFactory.AllowedOrigin, res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", res.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }

    [Fact]
    public async Task Unlisted_Origin_Gets_No_Cors_Header()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/tenants");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        req.Headers.Add("Origin", "https://evil.example");

        var res = await client.SendAsync(req);

        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
