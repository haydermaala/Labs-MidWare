using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ControlPlane.Api.Tests;

/// <summary>P6/P7 two-party tenant offboarding: a distinct approver begins the
/// offboarding pipeline (dynamic SoD); the pipeline is cancellable during cooling-off
/// and completed by a separate archive step; an archived tenant is terminal.</summary>
public sealed class PlatformOffboardServiceTests
{
    private sealed class Factory(string name) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    private static PlatformOffboardService New() =>
        new(new Factory($"offboard_{Guid.NewGuid():N}"), TimeProvider.System);

    [Fact]
    public void Approve_Needs_A_Distinct_Party_And_Yields_The_Tenant()
    {
        var svc = New();
        var req = svc.Request("ten_1", "u_req", "end of contract");

        // The requester may not approve their own offboarding request.
        Assert.Equal(PlatformOffboardService.DecideOutcome.SameParty, svc.Approve(req.Id, "u_req").Outcome);

        // A distinct approver executes it — the endpoint uses the returned tenant id.
        var (outcome, tenantId) = svc.Approve(req.Id, "u_other");
        Assert.Equal(PlatformOffboardService.DecideOutcome.Ok, outcome);
        Assert.Equal("ten_1", tenantId);

        // Re-deciding a decided request fails.
        Assert.Equal(PlatformOffboardService.DecideOutcome.NotPending, svc.Approve(req.Id, "u_third").Outcome);
    }

    [Fact]
    public void Reject_And_Pending()
    {
        var svc = New();
        var req = svc.Request("ten_1", "u_req", "");
        Assert.Single(svc.Pending());
        Assert.Equal(PlatformOffboardService.DecideOutcome.Ok, svc.Reject(req.Id, "u_sec"));
        Assert.Empty(svc.Pending());
        Assert.Equal(PlatformOffboardService.DecideOutcome.NotFound, svc.Approve("pof_ghost", "u").Outcome);
    }

    [Fact]
    public void Offboarding_Is_A_Cancellable_Pipeline_Ending_In_A_Terminal_Archive()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryControlPlaneStore(clock);
        var tenant = store.CreateTenant("Terminal Co");

        // Approval BEGINS offboarding: the tenant leaves active but is NOT yet terminal.
        Assert.Equal(TenantTransitionOutcome.Ok,
            store.TransitionTenant(tenant.Id, TenantLifecycleOperation.BeginOffboarding));
        var offboarding = store.FindTenant(tenant.Id)!;
        Assert.False(offboarding.Active);
        Assert.False(offboarding.Offboarded);
        Assert.Equal(nameof(TenantStatus.Offboarding), offboarding.Status);

        // A plain reactivate cannot resurrect a tenant mid-pipeline…
        Assert.False(store.ReactivateTenant(tenant.Id));
        // …but cancelling offboarding during cooling-off returns it to active.
        Assert.Equal(TenantTransitionOutcome.Ok,
            store.TransitionTenant(tenant.Id, TenantLifecycleOperation.CancelOffboarding));
        Assert.True(store.FindTenant(tenant.Id)!.Active);

        // Re-begin, then complete the pipeline to the terminal archived state — after the
        // cooling-off window has elapsed (the timed guard is covered on its own below).
        store.TransitionTenant(tenant.Id, TenantLifecycleOperation.BeginOffboarding);
        clock.Advance(OffboardingPolicy.CoolingOff + TimeSpan.FromMinutes(1));
        Assert.Equal(TenantTransitionOutcome.Ok,
            store.TransitionTenant(tenant.Id, TenantLifecycleOperation.Archive));
        var archived = store.FindTenant(tenant.Id)!;
        Assert.True(archived.Offboarded);
        Assert.Equal(nameof(TenantStatus.Archived), archived.Status);

        // Archived is terminal: neither reactivate nor any further transition applies.
        Assert.False(store.ReactivateTenant(tenant.Id));
        Assert.Equal(TenantTransitionOutcome.InvalidTransition,
            store.TransitionTenant(tenant.Id, TenantLifecycleOperation.CancelOffboarding));
        // Transitioning an unknown tenant is NotFound.
        Assert.Equal(TenantTransitionOutcome.NotFound,
            store.TransitionTenant("ten_ghost", TenantLifecycleOperation.BeginOffboarding));
    }

    [Fact]
    public void Archive_Is_Blocked_By_Cooling_Off_And_By_Legal_Hold()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryControlPlaneStore(clock);
        var t = store.CreateTenant("Hold Co");

        store.TransitionTenant(t.Id, TenantLifecycleOperation.BeginOffboarding);
        // The cooling-off window is open → archiving is refused (§10.3, no immediate hard-delete).
        Assert.Equal(TenantTransitionOutcome.CoolingOff,
            store.TransitionTenant(t.Id, TenantLifecycleOperation.Archive));
        Assert.NotNull(store.FindTenant(t.Id)!.CoolingOffUntil);

        // Advance the clock past the cooling-off window.
        clock.Advance(OffboardingPolicy.CoolingOff + TimeSpan.FromMinutes(1));

        // A legal hold overrides archiving, even once cooling-off has elapsed.
        Assert.True(store.SetTenantLegalHold(t.Id, hold: true));
        Assert.Equal(TenantTransitionOutcome.LegalHold,
            store.TransitionTenant(t.Id, TenantLifecycleOperation.Archive));

        // Lift the hold → archive now completes to the terminal state.
        Assert.True(store.SetTenantLegalHold(t.Id, hold: false));
        Assert.Equal(TenantTransitionOutcome.Ok,
            store.TransitionTenant(t.Id, TenantLifecycleOperation.Archive));
        var archived = store.FindTenant(t.Id)!;
        Assert.Equal(nameof(TenantStatus.Archived), archived.Status);
        Assert.Null(archived.CoolingOffUntil);
    }

    [Fact]
    public void Cooling_Off_Window_Follows_The_Provided_Retention()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryControlPlaneStore(clock);
        var t = store.CreateTenant("Retention Co");
        var window = TimeSpan.FromDays(90); // a longer plan's retention entitlement

        store.TransitionTenant(t.Id, TenantLifecycleOperation.BeginOffboarding, window);
        Assert.Equal(clock.GetUtcNow() + window, store.FindTenant(t.Id)!.CoolingOffUntil);

        // 60 days in (< 90) → archive still blocked.
        clock.Advance(TimeSpan.FromDays(60));
        Assert.Equal(TenantTransitionOutcome.CoolingOff,
            store.TransitionTenant(t.Id, TenantLifecycleOperation.Archive));

        // Past the 90-day window → archive allowed.
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(TenantTransitionOutcome.Ok,
            store.TransitionTenant(t.Id, TenantLifecycleOperation.Archive));
    }
}
