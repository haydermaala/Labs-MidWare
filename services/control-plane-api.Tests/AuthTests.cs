using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ControlPlane.Api.Tests;

/// <summary>Hosts the API with signup enabled and a roomy login rate limit so
/// functional tests never trip the guessing defense.</summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:AdminToken"] = "test-admin",
                ["ControlPlane:AllowSignup"] = "true",
                ["ControlPlane:LoginRatePermit"] = "1000",
            }));
    }
}

public sealed class AuthTests : IClassFixture<AuthApiFactory>
{
    private readonly AuthApiFactory _factory;

    public AuthTests(AuthApiFactory factory) => _factory = factory;

    private sealed record UserDto(string Id, string Email, bool EmailVerified, bool Active);
    private sealed record LoginDto(string SessionToken, DateTimeOffset ExpiresAt, UserDto User);
    private sealed record SessionDto(string Id, bool Current);

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return client;
    }

    private HttpClient SessionClient(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<LoginDto> CreateAndLogin(string email)
    {
        var admin = AdminClient();
        var created = await admin.PostAsJsonAsync("/api/admin/users",
            new { email, password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<LoginDto>())!;
    }

    [Fact]
    public async Task Login_With_Unknown_Email_Is_Generic_Unauthorized()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "nobody@example.test", password = "does not matter 123" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Admin_Creates_User_Then_Login_Sets_Cookie_And_Me_Works()
    {
        var login = await CreateAndLogin("tech1@example.test");

        Assert.StartsWith("ses_", login.SessionToken, StringComparison.Ordinal);
        Assert.False(login.User.EmailVerified);

        var me = await SessionClient(login.SessionToken).GetFromJsonAsync<UserDto>("/api/auth/me");
        Assert.Equal("tech1@example.test", me!.Email);
    }

    [Fact]
    public async Task Login_Response_Sets_HttpOnly_Secure_Cookie()
    {
        var admin = AdminClient();
        await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "cookie@example.test", password = "correct horse battery staple" });
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "cookie@example.test", password = "correct horse battery staple" });

        var setCookie = res.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("lc_session=", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie.ToLowerInvariant());
        Assert.Contains("secure", setCookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", setCookie.ToLowerInvariant());
    }

    [Fact]
    public async Task Wrong_Password_Is_Generic_Unauthorized()
    {
        var admin = AdminClient();
        await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "wrongpw@example.test", password = "correct horse battery staple" });
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "wrongpw@example.test", password = "not the password 12" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Duplicate_Email_Is_Conflict_For_Admin_Create()
    {
        var admin = AdminClient();
        await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "dup@example.test", password = "correct horse battery staple" });
        var second = await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "DUP@example.test ", password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Short_Password_Is_Rejected()
    {
        var admin = AdminClient();
        var res = await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "short@example.test", password = "tooshort" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Logout_Revokes_The_Session()
    {
        var login = await CreateAndLogin("logout@example.test");
        var client = SessionClient(login.SessionToken);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task RevokeAll_Kills_Other_Sessions()
    {
        var first = await CreateAndLogin("multi@example.test");
        var second = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "multi@example.test", password = "correct horse battery staple" });
        var secondLogin = (await second.Content.ReadFromJsonAsync<LoginDto>())!;

        var sessions = await SessionClient(first.SessionToken)
            .GetFromJsonAsync<List<SessionDto>>("/api/auth/sessions");
        Assert.True(sessions!.Count >= 2);
        Assert.Single(sessions, s => s.Current);

        var revoke = await SessionClient(first.SessionToken).PostAsync("/api/auth/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SessionClient(secondLogin.SessionToken).GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Auth_Endpoints_Reject_Anonymous()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Admin_User_Creation_Requires_Admin_Token()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/admin/users",
            new { email = "nope@example.test", password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

/// <summary>Signup stays hidden (404) unless business policy enables it, and the
/// login limiter throttles credential guessing.</summary>
public sealed class AuthPolicyTests
{
    private sealed class ClosedSignupFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ControlPlane:AdminToken"] = "test-admin",
                    ["ControlPlane:LoginRatePermit"] = "3",
                }));
        }
    }

    [Fact]
    public async Task Signup_Is_NotFound_When_Disabled()
    {
        using var factory = new ClosedSignupFactory();
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/signup",
            new { email = "x@example.test", password = "correct horse battery staple" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Login_Attempts_Beyond_The_Window_Are_Throttled()
    {
        using var factory = new ClosedSignupFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var res = await client.PostAsJsonAsync("/api/auth/login",
                new { email = "guess@example.test", password = "guess attempt 0001" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        var throttled = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "guess@example.test", password = "guess attempt 0001" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }
}
