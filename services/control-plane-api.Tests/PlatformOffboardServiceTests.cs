using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

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
        var store = new InMemoryControlPlaneStore(TimeProvider.System);
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

        // Re-begin, then complete the pipeline to the terminal archived state.
        store.TransitionTenant(tenant.Id, TenantLifecycleOperation.BeginOffboarding);
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
}
