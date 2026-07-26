using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>
/// The tenant lifecycle state machine (prompt §10, §13.1): guarded, deny-by-default
/// transitions; which states permit operation; named-operation intent mapping; and the
/// backfill from the legacy (Active, Offboarded) booleans.
/// </summary>
public sealed class TenantLifecycleTests
{
    [Theory]
    [InlineData(TenantStatus.Provisioning, TenantStatus.Active)]
    [InlineData(TenantStatus.Provisioning, TenantStatus.Trial)]
    [InlineData(TenantStatus.Trial, TenantStatus.Active)]
    [InlineData(TenantStatus.Active, TenantStatus.Grace)]
    [InlineData(TenantStatus.Active, TenantStatus.Suspended)]
    [InlineData(TenantStatus.Grace, TenantStatus.Active)]
    [InlineData(TenantStatus.Suspended, TenantStatus.Active)]
    [InlineData(TenantStatus.Active, TenantStatus.Offboarding)]
    [InlineData(TenantStatus.Offboarding, TenantStatus.Active)]
    [InlineData(TenantStatus.Offboarding, TenantStatus.Archived)]
    public void CanTransition_Allows_Legal_Edges(TenantStatus from, TenantStatus to) =>
        Assert.True(TenantLifecycle.CanTransition(from, to));

    [Theory]
    [InlineData(TenantStatus.Active, TenantStatus.Provisioning)] // no going back to provisioning
    [InlineData(TenantStatus.Suspended, TenantStatus.Grace)]     // restore is to active, not grace
    [InlineData(TenantStatus.Archived, TenantStatus.Active)]     // terminal
    [InlineData(TenantStatus.Archived, TenantStatus.Offboarding)]
    [InlineData(TenantStatus.Provisioning, TenantStatus.Archived)] // can't skip the pipeline
    [InlineData(TenantStatus.Active, TenantStatus.Active)]       // self is not an edge
    public void CanTransition_Denies_Illegal_Edges(TenantStatus from, TenantStatus to) =>
        Assert.False(TenantLifecycle.CanTransition(from, to));

    [Fact]
    public void Archived_Is_The_Only_Terminal_State()
    {
        foreach (var s in Enum.GetValues<TenantStatus>())
        {
            Assert.Equal(s == TenantStatus.Archived, TenantLifecycle.IsTerminal(s));
        }
    }

    [Theory]
    [InlineData(TenantStatus.Trial, true)]
    [InlineData(TenantStatus.Active, true)]
    [InlineData(TenantStatus.Grace, true)]
    [InlineData(TenantStatus.Provisioning, false)]
    [InlineData(TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.Offboarding, false)]
    [InlineData(TenantStatus.Archived, false)]
    public void AllowsOperation_Only_In_Live_States(TenantStatus status, bool allowed) =>
        Assert.Equal(allowed, TenantLifecycle.AllowsOperation(status));

    [Theory]
    [InlineData(TenantStatus.Suspended, TenantLifecycleOperation.Restore, TenantStatus.Active)]
    [InlineData(TenantStatus.Active, TenantLifecycleOperation.Suspend, TenantStatus.Suspended)]
    [InlineData(TenantStatus.Active, TenantLifecycleOperation.BeginOffboarding, TenantStatus.Offboarding)]
    [InlineData(TenantStatus.Offboarding, TenantLifecycleOperation.CancelOffboarding, TenantStatus.Active)]
    [InlineData(TenantStatus.Offboarding, TenantLifecycleOperation.Archive, TenantStatus.Archived)]
    [InlineData(TenantStatus.Grace, TenantLifecycleOperation.Activate, TenantStatus.Active)]
    [InlineData(TenantStatus.Active, TenantLifecycleOperation.EnterGrace, TenantStatus.Grace)]
    public void Target_Maps_Named_Operations(TenantStatus from, TenantLifecycleOperation op, TenantStatus expected) =>
        Assert.Equal(expected, TenantLifecycle.Target(from, op));

    [Theory]
    [InlineData(TenantStatus.Active, TenantLifecycleOperation.Restore)]        // restore only from suspended
    [InlineData(TenantStatus.Archived, TenantLifecycleOperation.CancelOffboarding)]
    [InlineData(TenantStatus.Active, TenantLifecycleOperation.Archive)]        // archive only from offboarding
    [InlineData(TenantStatus.Provisioning, TenantLifecycleOperation.EnterGrace)]
    public void Target_Is_Null_When_Operation_Not_Applicable(TenantStatus from, TenantLifecycleOperation op) =>
        Assert.Null(TenantLifecycle.Target(from, op));

    [Fact]
    public void Every_Operation_Target_Is_Itself_A_Legal_Edge()
    {
        // Intent and the transition graph must not drift: whenever an operation yields a
        // target from some state, that (from → target) must be a legal CanTransition edge.
        foreach (var from in Enum.GetValues<TenantStatus>())
        {
            foreach (var op in Enum.GetValues<TenantLifecycleOperation>())
            {
                var target = TenantLifecycle.Target(from, op);
                if (target is { } to)
                {
                    Assert.True(TenantLifecycle.CanTransition(from, to),
                        $"{op} from {from} targets {to}, which is not a legal edge.");
                }
            }
        }
    }

    [Theory]
    [InlineData(true, false, TenantStatus.Active)]
    [InlineData(false, false, TenantStatus.Suspended)]
    [InlineData(true, true, TenantStatus.Archived)]   // offboarded was terminal
    [InlineData(false, true, TenantStatus.Archived)]
    public void FromLegacy_Maps_The_Old_Booleans(bool active, bool offboarded, TenantStatus expected) =>
        Assert.Equal(expected, TenantLifecycle.FromLegacy(active, offboarded));
}
