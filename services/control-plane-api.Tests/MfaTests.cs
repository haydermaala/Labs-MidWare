using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>TOTP MFA enrollment, challenge login, and recovery codes (C4).
/// Tests compute real codes with the same RFC 6238 implementation.</summary>
public sealed class MfaTests : IClassFixture<AuthApiFactory>
{
    private readonly AuthApiFactory _factory;

    public MfaTests(AuthApiFactory factory) => _factory = factory;

    private sealed record UserDto(string Id, string Email, bool MfaEnabled);
    private sealed record LoginDto(string? SessionToken, bool MfaRequired, string? MfaToken);
    private sealed record SetupDto(string Secret, string ProvisioningUri);
    private sealed record RecoveryDto(List<string> RecoveryCodes);
    private sealed record SessionResultDto(string SessionToken);

    private HttpClient Session(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<string> NewUserSession(string email)
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        await admin.PostAsJsonAsync("/api/admin/users", new { email, password = "correct horse battery staple" });
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        return (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken!;
    }

    /// <summary>Enroll + arm MFA; returns (secret, recoveryCodes, session).</summary>
    private async Task<(string Secret, List<string> Recovery, string Session)> EnableMfa(string email)
    {
        var session = await NewUserSession(email);
        var client = Session(session);
        var setup = await (await client.PostAsync("/api/auth/mfa/setup", null)).Content.ReadFromJsonAsync<SetupDto>();
        Assert.Contains("otpauth://totp/LabConnect:", setup!.ProvisioningUri);
        var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable",
            new { code = Totp.Compute(setup.Secret, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var recovery = (await enable.Content.ReadFromJsonAsync<RecoveryDto>())!;
        Assert.Equal(8, recovery.RecoveryCodes.Count);
        return (setup.Secret, recovery.RecoveryCodes, session);
    }

    [Fact]
    public async Task Enable_Requires_A_Valid_Code_From_The_Enrolled_Secret()
    {
        var session = await NewUserSession("mfa-badcode@example.test");
        var client = Session(session);
        await client.PostAsync("/api/auth/mfa/setup", null);
        var res = await client.PostAsJsonAsync("/api/auth/mfa/enable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Mfa_Login_Requires_The_Second_Factor()
    {
        var (secret, _, _) = await EnableMfa("mfa-login@example.test");

        // Password alone yields a challenge, not a session.
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "mfa-login@example.test", password = "correct horse battery staple" });
        var outcome = (await login.Content.ReadFromJsonAsync<LoginDto>())!;
        Assert.True(outcome.MfaRequired);
        Assert.Null(outcome.SessionToken);

        // Wrong code: challenge consumed, 401.
        var bad = await _factory.CreateClient().PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaToken = outcome.MfaToken, code = "123456" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // The consumed challenge cannot be retried even with the right code.
        var replay = await _factory.CreateClient().PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaToken = outcome.MfaToken, code = Totp.Compute(secret, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Fresh challenge + correct code → session works.
        var login2 = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "mfa-login@example.test", password = "correct horse battery staple" });
        var outcome2 = (await login2.Content.ReadFromJsonAsync<LoginDto>())!;
        var verify = await _factory.CreateClient().PostAsJsonAsync("/api/auth/mfa/verify",
            new { mfaToken = outcome2.MfaToken, code = Totp.Compute(secret, DateTimeOffset.UtcNow) });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var result = (await verify.Content.ReadFromJsonAsync<SessionResultDto>())!;
        var me = await Session(result.SessionToken).GetFromJsonAsync<UserDto>("/api/auth/me");
        Assert.True(me!.MfaEnabled);
    }

    [Fact]
    public async Task Recovery_Code_Completes_Login_Exactly_Once()
    {
        var (_, recovery, _) = await EnableMfa("mfa-recovery@example.test");

        async Task<HttpResponseMessage> Recover(string code)
        {
            var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
                new { email = "mfa-recovery@example.test", password = "correct horse battery staple" });
            var outcome = (await login.Content.ReadFromJsonAsync<LoginDto>())!;
            return await _factory.CreateClient().PostAsJsonAsync("/api/auth/mfa/recover",
                new { mfaToken = outcome.MfaToken, recoveryCode = code });
        }

        Assert.Equal(HttpStatusCode.OK, (await Recover(recovery[0])).StatusCode);
        // The same code again is dead.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Recover(recovery[0])).StatusCode);
        // A different unused code still works.
        Assert.Equal(HttpStatusCode.OK, (await Recover(recovery[1])).StatusCode);
    }

    [Fact]
    public async Task Disable_Requires_Current_Code_And_Restores_Plain_Login()
    {
        var (secret, _, session) = await EnableMfa("mfa-disable@example.test");
        var client = Session(session);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/mfa/disable", new { code = "999999" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/mfa/disable",
                new { code = Totp.Compute(secret, DateTimeOffset.UtcNow) })).StatusCode);

        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = "mfa-disable@example.test", password = "correct horse battery staple" });
        var outcome = (await login.Content.ReadFromJsonAsync<LoginDto>())!;
        Assert.False(outcome.MfaRequired);
        Assert.NotNull(outcome.SessionToken);
    }

    [Fact]
    public void Totp_Reference_Vector_RFC6238_Style()
    {
        // Deterministic sanity: same secret+time → same 6-digit code; adjacent
        // steps differ; verification honors the ±1-step window.
        var secret = Totp.NewSecret();
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.Compute(secret, at);
        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(code, Totp.Compute(secret, at));
        Assert.True(Totp.Verify(secret, code, at.AddSeconds(29)));
        Assert.False(Totp.Verify(secret, Totp.Compute(secret, at.AddSeconds(-120)), at));
    }
}
