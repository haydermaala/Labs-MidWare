using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ControlPlane.Api.Tests;

/// <summary>
/// A host with its own in-memory database.
///
/// The default in-memory store is keyed by a fixed name, so every test host shares one
/// database and every test class sees every other class's writes. That is harmless for
/// assertions scoped to an id the test just created, and quietly fatal for assertions
/// about GLOBAL append-only tables — platform_security_events above all, where a test
/// that counts rows before and after an action is really measuring that action plus
/// whatever the rest of the suite happened to do in between.
///
/// Use this fixture for any test whose subject is a global counter or log.
/// </summary>
public sealed class IsolatedApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"labconnect-test-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:AdminToken"] = "test-admin",
                ["ControlPlane:LoginRatePermit"] = "1000",
                ["ControlPlane:PublicBaseUrl"] = "https://lc.example.test",
                ["ControlPlane:InMemoryDatabaseName"] = _databaseName,
            }));
}
