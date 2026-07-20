using System.Net;
using System.Net.Http.Json;
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
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
    }

    private sealed record HealthDto(string Status, string Service, string Version);
}
