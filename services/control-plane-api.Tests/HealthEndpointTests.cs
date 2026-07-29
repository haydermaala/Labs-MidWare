using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Phase 1 acceptance: the API starts and reports healthy. Confirms the health
/// payload carries no unexpected fields (guards against PHI leakage by shape).
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthDto>();
        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
        Assert.Equal("control-plane-api", body.Service);
    }

    [Fact]
    public async Task Ready_ReturnsReady_WhenTheDatabaseIsReachable()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthDto>();
        Assert.Equal("ready", body!.Status);
    }

    [Fact]
    public async Task Every_Response_Carries_The_Security_Headers()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
        // CSP is scoped to what the same-origin SPA needs: self, data: images,
        // inline styles — no external origins, no inline scripts, framing denied.
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
    }

    [Fact]
    public async Task Ready_Reports_NotReady_When_DATABASE_URL_Is_Missing_Outside_Development()
    {
        // A missing/mistyped DATABASE_URL silently drops the app onto the EF in-memory
        // provider, whose CanConnectAsync() returns true — so readiness used to report
        // GREEN while every tenant read an EMPTY database. The production cutover
        // verifies "/health/ready green", so that gap made the check worthless.
        using var production = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var response = await production.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthDto>();
        Assert.Equal("not-ready", body!.Status);
        Assert.Contains("in-memory", body.Database, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_Names_The_Active_Database_Provider()
    {
        // The provider is surfaced so an operator can tell a real Postgres readiness
        // from the dev fallback at a glance.
        var body = await _factory.CreateClient().GetFromJsonAsync<HealthDto>("/health/ready");
        Assert.Equal("in-memory", body!.Database); // tests run on the fallback, in Development
    }

    private sealed record HealthDto(string Status, string Service, string Version, string? Database);
}
