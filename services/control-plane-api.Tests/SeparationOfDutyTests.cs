using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>Separation of duty (P3, ADR 0020 §5): static (no one subject holds
/// both sides of a rule) and dynamic (approver ≠ requester).</summary>
public sealed class SeparationOfDutyTests
{
    // A subject who can publish config must not also be able to approve a clinical
    // release (illustrative; rules are per-tenant data in practice).
    private static readonly SodRule[] Rules =
    [
        new("sod_1", "author-vs-approver", Permissions.FleetConfigPublish.Key, Permissions.MembersMemberChangeRole.Key),
    ];

    [Fact]
    public void Static_Violation_Is_Detected_When_Both_Sides_Are_Held()
    {
        var held = new HashSet<string> { Permissions.FleetConfigPublish.Key, Permissions.MembersMemberChangeRole.Key };
        var violations = SeparationOfDuty.StaticViolations(held, Rules);
        Assert.Single(violations);
        Assert.Equal("sod_1", violations.First().Id);
    }

    [Fact]
    public void Holding_Only_One_Side_Is_Not_A_Violation()
    {
        var held = new HashSet<string> { Permissions.FleetConfigPublish.Key };
        Assert.Empty(SeparationOfDuty.StaticViolations(held, Rules));
    }

    [Fact]
    public void WouldViolate_Gates_A_Grant_Regardless_Of_Rule_Side_Order()
    {
        var held = new HashSet<string> { Permissions.FleetConfigPublish.Key };
        // Adding the conflicting counterpart is blocked...
        Assert.True(SeparationOfDuty.WouldViolate(held, Permissions.MembersMemberChangeRole.Key, Rules));
        // ...but an unrelated permission is fine.
        Assert.False(SeparationOfDuty.WouldViolate(held, Permissions.FleetGatewayView.Key, Rules));
    }

    [Fact]
    public void Counterpart_Resolves_Either_Side()
    {
        var rule = Rules[0];
        Assert.Equal(Permissions.MembersMemberChangeRole.Key, rule.Counterpart(Permissions.FleetConfigPublish.Key));
        Assert.Equal(Permissions.FleetConfigPublish.Key, rule.Counterpart(Permissions.MembersMemberChangeRole.Key));
        Assert.Null(rule.Counterpart(Permissions.FleetGatewayView.Key));
    }

    [Fact]
    public void Dynamic_Sod_Requires_Distinct_Parties()
    {
        Assert.True(SeparationOfDuty.IsDistinctParty("usr_a", "usr_b"));
        Assert.False(SeparationOfDuty.IsDistinctParty("usr_a", "usr_a"));
    }
}
