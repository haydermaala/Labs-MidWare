using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>The two-party approval flow (P3, dynamic SoD). The load-bearing rule is
/// that the approver must be a DISTINCT party from the requester
/// (SeparationOfDuty.IsDistinctParty); the rest covers the request lifecycle.</summary>
public sealed class ApprovalServiceTests
{
    private sealed class Factory(string name) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    private static ApprovalService New() =>
        new(new Factory($"appr_{Guid.NewGuid():N}"), TimeProvider.System);

    private static readonly string Deactivate = Permissions.TenantDeactivate.Key;

    [Fact]
    public void Create_Requires_An_Approval_Gated_Permission()
    {
        var svc = New();
        Assert.NotNull(svc.Create("ten_1", Deactivate, null, "u_req", null, "shutting down"));
        // A permission that is not approval-gated cannot open a request.
        Assert.Null(svc.Create("ten_1", Permissions.FleetGatewayView.Key, null, "u_req", null, null));
        Assert.Null(svc.Create("ten_1", "not.a.permission", null, "u_req", null, null));
    }

    [Fact]
    public void Approve_Requires_A_Distinct_Party()
    {
        var svc = New();
        var req = svc.Create("ten_1", Deactivate, null, "u_req", null, null)!;

        // The requester may not approve their own request (author ≠ approver).
        Assert.Equal(ApprovalService.DecideOutcome.SameParty, svc.Approve("ten_1", req.Id, "u_req"));
        // A distinct party can.
        Assert.Equal(ApprovalService.DecideOutcome.Ok, svc.Approve("ten_1", req.Id, "u_other"));
        // Re-deciding a decided request fails.
        Assert.Equal(ApprovalService.DecideOutcome.NotPending, svc.Approve("ten_1", req.Id, "u_third"));
    }

    [Fact]
    public void Approve_Unknown_Or_Wrong_Tenant_Is_NotFound()
    {
        var svc = New();
        var req = svc.Create("ten_1", Deactivate, null, "u_req", null, null)!;
        Assert.Equal(ApprovalService.DecideOutcome.NotFound, svc.Approve("ten_1", "apr_ghost", "u_o"));
        Assert.Equal(ApprovalService.DecideOutcome.NotFound, svc.Approve("ten_other", req.Id, "u_o"));
    }

    [Fact]
    public void Reject_Allows_The_Requester_To_Cancel()
    {
        var svc = New();
        var req = svc.Create("ten_1", Deactivate, null, "u_req", null, null)!;
        // Reject does not require a distinct party, so the requester may cancel.
        Assert.Equal(ApprovalService.DecideOutcome.Ok, svc.Reject("ten_1", req.Id, "u_req"));
        Assert.Empty(svc.Pending("ten_1"));
    }

    [Fact]
    public void Pending_Lists_Only_Undecided()
    {
        var svc = New();
        var a = svc.Create("ten_1", Deactivate, null, "u_a", null, null)!;
        svc.Create("ten_1", Deactivate, null, "u_b", null, null);
        Assert.Equal(2, svc.Pending("ten_1").Count);
        svc.Approve("ten_1", a.Id, "u_x");
        Assert.Single(svc.Pending("ten_1"));
    }
}
