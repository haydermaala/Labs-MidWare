using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ControlPlane.Api.Tests;

/// <summary>Captures outbound email in memory so tests can read the links.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public List<OutboundEmail> Sent { get; } = [];

    public Task SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        lock (Sent) { Sent.Add(email); }
        return Task.CompletedTask;
    }
}

public sealed class EmailApiFactory : WebApplicationFactory<Program>
{
    public CapturingEmailSender Outbox { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlPlane:AdminToken"] = "test-admin",
                ["ControlPlane:LoginRatePermit"] = "1000",
                ["ControlPlane:PublicBaseUrl"] = "https://lc.example.test",
            }));
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IEmailSender>(Outbox));
    }
}

public sealed class EmailFlowTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;

    public EmailFlowTests(EmailApiFactory factory) => _factory = factory;

    private sealed record LoginDto(string SessionToken);
    private sealed record UserDto(string Email, bool EmailVerified);

    private static string TokenFromLink(OutboundEmail email)
    {
        var match = Regex.Match(email.TextBody, @"token=([A-Za-z0-9_\-]+)");
        Assert.True(match.Success, "email must contain a tokenized link");
        return match.Groups[1].Value;
    }

    private async Task<(string Email, string Session)> NewUser(string email)
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/users",
            new { email, password = "correct horse battery staple" })).StatusCode);
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        return (email, (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken);
    }

    private HttpClient WithSession(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Verification_Email_Round_Trip_Marks_Email_Verified()
    {
        var (email, session) = await NewUser("verify@example.test");
        var client = WithSession(session);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/auth/send-verification", null)).StatusCode);
        var sent = _factory.Outbox.Sent.Last(e => e.To == email);
        Assert.Contains("https://lc.example.test/verify-email?token=", sent.TextBody);

        var verify = await _factory.CreateClient().PostAsJsonAsync("/api/auth/verify-email",
            new { token = TokenFromLink(sent) });
        Assert.Equal(HttpStatusCode.NoContent, verify.StatusCode);

        var me = await client.GetFromJsonAsync<UserDto>("/api/auth/me");
        Assert.True(me!.EmailVerified);

        // Single-use: replaying the same link fails.
        var replay = await _factory.CreateClient().PostAsJsonAsync("/api/auth/verify-email",
            new { token = TokenFromLink(sent) });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Password_Reset_Round_Trip_Rotates_Password_And_Revokes_Sessions()
    {
        var (email, session) = await NewUser("reset@example.test");

        Assert.Equal(HttpStatusCode.Accepted, (await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/forgot-password", new { email })).StatusCode);
        var token = TokenFromLink(_factory.Outbox.Sent.Last(e => e.To == email));

        var reset = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password",
            new { token, newPassword = "an entirely new passphrase" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // Old session is revoked; old password fails; new password works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await WithSession(session).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { email, password = "correct horse battery staple" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { email, password = "an entirely new passphrase" })).StatusCode);
    }

    [Fact]
    public async Task Forgot_Password_Is_Silent_For_Unknown_Email()
    {
        var before = _factory.Outbox.Sent.Count;
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password",
            new { email = "ghost@example.test" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(before, _factory.Outbox.Sent.Count);
    }

    [Fact]
    public async Task Reset_With_Bogus_Token_Fails_Generically()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password",
            new { token = "rtk_notreal", newPassword = "an entirely new passphrase" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
