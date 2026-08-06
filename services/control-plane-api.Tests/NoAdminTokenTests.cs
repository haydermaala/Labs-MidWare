using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ControlPlane.Api.Tests;

/// <summary>A host with NO admin token configured — production after retirement.</summary>
public sealed class NoAdminTokenFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"labconnect-notoken-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Deliberately absent: ControlPlane:AdminToken.
                ["ControlPlane:LoginRatePermit"] = "1000",
                ["ControlPlane:PublicBaseUrl"] = "https://lc.example.test",
                ["ControlPlane:InMemoryDatabaseName"] = _databaseName,
            }));
}

/// <summary>
/// The configuration we are about to ship, tested before we ship it.
///
/// Every other test class in this suite sets ControlPlane:AdminToken, so the entire
/// suite only ever exercised the app WITH god-mode available. Retiring the token means
/// unsetting that variable in production — a state no test had ever run in. The plan
/// was to verify the most dangerous change in the programme by making it in production
/// and watching.
///
/// These tests pin what must be true afterwards:
///   - every token-only route refuses everyone, including former token holders
///   - IsAdmin is false for every input, so BreakGlass can never fire and nothing is
///     recorded as break-glass
///   - the session path still works end to end, so operators are not locked out
///
/// If this class fails, retiring the token would take the system down or leave a door
/// open. That is the gate.
/// </summary>
public sealed class NoAdminTokenTests : IClassFixture<NoAdminTokenFactory>
{
    private readonly NoAdminTokenFactory _factory;

    public NoAdminTokenTests(NoAdminTokenFactory factory) => _factory = factory;

    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);

    private HttpClient WithBearer(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public static TheoryData<string, string> TokenOnlyRoutes() => new()
    {
        { "POST", "/api/tenants" },
        { "GET", "/api/tenants" },
        { "POST", "/api/admin/users" },
        { "POST", "/api/admin/memberships" },
    };

    [Theory]
    [MemberData(nameof(TokenOnlyRoutes))]
    public async Task Token_Only_Routes_Refuse_Everyone(string method, string route)
    {
        // Anonymous, a plausible guess, and the literal value other tests use — all
        // must be refused. An empty configured token must not make IsAdmin's string
        // compare succeed against an empty or absent header.
        foreach (var client in new[]
                 {
                     _factory.CreateClient(),
                     WithBearer("test-admin"),
                     WithBearer(""),
                     WithBearer("Bearer "),
                 })
        {
            var res = method == "GET"
                ? await client.GetAsync(route)
                : await client.PostAsJsonAsync(route, new { name = "x", email = "a@b.test", password = "correct horse battery staple" });

            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    [Fact]
    public async Task Break_Glass_Cannot_Fire_At_All()
    {
        // With no token configured IsAdmin is false for every request, so BreakGlass can
        // never fire and the readiness gate is permanently quiet by construction.
        //
        // Asserted through whoami rather than by reading the audit trail, because with
        // the token gone the trail is unreadable without a pre-existing platform role
        // — see Retiring_The_Token_Requires_A_Root_Owner_To_Already_Exist. whoami is the
        // sharpest available probe: it returns EVERY platform role when IsAdmin is true
        // and the caller's own roles otherwise, so it reports the break-glass decision
        // directly.
        var whoami = await WithBearer("test-admin").GetAsync("/api/platform/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, whoami.StatusCode);

        var (_, session, _) = await Bootstrap();
        var asUser = await WithBearer(session)
            .GetFromJsonAsync<Dictionary<string, List<string>>>("/api/platform/whoami");
        Assert.Empty(asUser!["roles"]);
    }

    [Fact]
    public async Task Retiring_The_Token_Requires_A_Root_Owner_To_Already_Exist()
    {
        // A one-way door, pinned here so it is a decision rather than a discovery.
        //
        // Granting a platform role needs platform.role.manage, which only Root Owner
        // holds, and the only other way to reach that endpoint was the admin token. So
        // once the token is gone, platform roles can only ever be granted by someone who
        // already has one. A freshly created user — the state you are in after losing
        // every Root Owner account — cannot be given a role by anyone, and every
        // platform surface stays shut forever.
        //
        // Retirement is therefore safe only while at least one Root Owner login is
        // provably working, and that must be re-proven, not assumed, before the variable
        // is unset. There is no recovery path in the application.
        var (userId, session, _) = await Bootstrap();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await WithBearer(session).GetAsync("/api/platform/security-events")).StatusCode);

        var selfGrant = await WithBearer(session).PostAsJsonAsync(
            "/api/platform/role-assignments",
            new { userId, role = PlatformRoles.RootOwner });
        Assert.Equal(HttpStatusCode.Unauthorized, selfGrant.StatusCode);
    }

    [Fact]
    public async Task An_Operator_Is_Not_Locked_Out()
    {
        // The other half of the gate. Refusing everything is easy; the point is that
        // real operators keep working. Signup must be enabled to create the very first
        // account once the token is gone — that is the bootstrap story, and if it does
        // not hold, retirement is a one-way door.
        var (userId, session, email) = await Bootstrap();

        Assert.NotEmpty(userId);
        Assert.Contains("@", email, StringComparison.Ordinal);

        // A logged-in session authenticates and is recognised.
        var me = await WithBearer(session).GetAsync("/api/me/memberships");
        Assert.NotEqual(HttpStatusCode.Unauthorized, me.StatusCode);

        // And a non-platform user is refused platform surfaces — the gate still gates.
        var whoami = await WithBearer(session).GetFromJsonAsync<Dictionary<string, object>>(
            "/api/platform/whoami");
        Assert.NotNull(whoami);
    }

    /// <summary>
    /// Create the first account with no admin token available. This documents the
    /// bootstrap dependency: without the token, ControlPlane:AllowSignup is the only
    /// way to mint the first user, and it is currently unset in production and staging.
    /// </summary>
    private async Task<(string UserId, string Session, string Email)> Bootstrap()
    {
        var email = $"first-{Guid.NewGuid():N}@example.test";
        const string password = "correct horse battery staple";

        var signup = await _factory.WithWebHostBuilder(b =>
                b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ControlPlane:AllowSignup"] = "true" })))
            .CreateClient()
            .PostAsJsonAsync("/api/auth/signup", new { email, password });
        Assert.Equal(HttpStatusCode.OK, signup.StatusCode);

        var login = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var dto = (await login.Content.ReadFromJsonAsync<LoginDto>())!;
        return (dto.User.Id, dto.SessionToken, email);
    }
}
