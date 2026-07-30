using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Step-up session plumbing (ADR 0019): a session's fresh-auth window expires and
/// is refreshed by re-verifying credentials. Exercised directly against AuthService
/// with a controllable clock (the HTTP tests use the real system clock).
/// </summary>
public sealed class AuthStepUpTests
{
    private sealed class Factory(string name) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    private const string Password = "correct horse battery";

    private static (AuthService Auth, FakeTimeProvider Clock) NewAuth()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        return (new AuthService(new Factory($"auth_{Guid.NewGuid():N}"), clock), clock);
    }

    [Fact]
    public void Fresh_Auth_Expires_After_The_Window_And_Step_Up_Refreshes_It()
    {
        var (auth, clock) = NewAuth();
        Assert.NotNull(auth.CreateUser("stepup@example.test", Password));
        var token = auth.Login("stepup@example.test", Password)!.Session!.SessionToken;

        var fresh = auth.Authenticate(token);
        Assert.True(fresh!.Value.FreshAuth);
        Assert.False(fresh.Value.MfaSatisfied); // password-only login

        clock.Advance(AuthService.StepUpWindow + TimeSpan.FromMinutes(1));
        Assert.False(auth.Authenticate(token)!.Value.FreshAuth); // window elapsed

        var stale = auth.Authenticate(token)!.Value;
        Assert.True(auth.StepUp(stale.SessionId, stale.User.Id, Password, code: null));
        Assert.True(auth.Authenticate(token)!.Value.FreshAuth); // refreshed by step-up
    }

    [Fact]
    public void Step_Up_Fails_On_A_Wrong_Password()
    {
        var (auth, _) = NewAuth();
        auth.CreateUser("stepup2@example.test", Password);
        var session = auth.Authenticate(auth.Login("stepup2@example.test", Password)!.Session!.SessionToken)!.Value;

        Assert.False(auth.StepUp(session.SessionId, session.User.Id, "wrong-password-abc", code: null));
    }
}
