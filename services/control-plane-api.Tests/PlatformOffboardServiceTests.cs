using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>P6 two-party tenant offboarding: a distinct approver executes the terminal
/// offboarding (dynamic SoD), and an offboarded tenant can never be reactivated.</summary>
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
    public void Offboarded_Tenant_Is_Terminal_And_Cannot_Be_Reactivated()
    {
        var store = new InMemoryControlPlaneStore(TimeProvider.System);
        var tenant = store.CreateTenant("Terminal Co");

        Assert.True(store.OffboardTenant(tenant.Id));
        var after = store.FindTenant(tenant.Id)!;
        Assert.False(after.Active);
        Assert.True(after.Offboarded);

        // Reactivation is refused for an offboarded tenant.
        Assert.False(store.ReactivateTenant(tenant.Id));
        Assert.False(store.FindTenant(tenant.Id)!.Active);
        // Offboarding an unknown tenant is false.
        Assert.False(store.OffboardTenant("ten_ghost"));
    }
}
