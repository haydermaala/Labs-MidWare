using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>Custom roles + delegation (P3, ADR 0020 §3): baseline and custom roles
/// resolve uniformly, and a grantor may only delegate delegable permissions they
/// themselves hold.</summary>
public sealed class CustomRolesTests
{
    private static readonly Dictionary<string, IReadOnlySet<string>> Custom = new(StringComparer.Ordinal)
    {
        // A tenant-defined "fleet-viewer": view gateways + config only.
        ["fleet-viewer"] = new HashSet<string>(StringComparer.Ordinal)
        {
            Permissions.FleetGatewayView.Key, Permissions.FleetConfigView.Key,
        },
    };

    [Fact]
    public void Baseline_Permissions_Match_The_Role_Matrix()
    {
        var owner = RoleGrants.BaselinePermissions(Roles.Owner);
        Assert.Equal(Permissions.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal), owner); // owner holds all

        var readOnly = RoleGrants.BaselinePermissions(Roles.ReadOnly);
        Assert.Contains(Permissions.FleetGatewayView.Key, readOnly);
        Assert.DoesNotContain(Permissions.FleetGatewayEnroll.Key, readOnly);
    }

    [Fact]
    public void Grants_Resolves_Baseline_And_Custom_Roles()
    {
        // Baseline (custom map ignored for a baseline role).
        Assert.True(RoleGrants.Grants(Roles.LabAdmin, Permissions.FleetGatewayEnroll.Key, Custom));
        Assert.False(RoleGrants.Grants(Roles.LabAdmin, Permissions.MembersMemberInvite.Key, Custom));

        // Custom role: only what its grant set contains.
        Assert.True(RoleGrants.Grants("fleet-viewer", Permissions.FleetGatewayView.Key, Custom));
        Assert.False(RoleGrants.Grants("fleet-viewer", Permissions.FleetGatewayEnroll.Key, Custom));

        // Unknown role → no grant.
        Assert.False(RoleGrants.Grants("ghost", Permissions.FleetGatewayView.Key, Custom));
    }

    [Fact]
    public void Delegation_Requires_A_Delegable_Permission_The_Grantor_Holds()
    {
        var tenantAdmin = RoleGrants.BaselinePermissions(Roles.TenantAdmin);

        // Holds + delegable → may delegate.
        Assert.True(Delegation.CanDelegate(tenantAdmin, Permissions.FleetGatewayEnroll.Key));
        // Does not hold (tenant-admin lacks billing) → may not delegate.
        Assert.False(Delegation.CanDelegate(tenantAdmin, Permissions.BillingSubscriptionManage.Key));

        // An owner holds tenant.deactivate but it is marked non-delegable.
        var owner = RoleGrants.BaselinePermissions(Roles.Owner);
        Assert.Contains(Permissions.TenantDeactivate.Key, owner);
        Assert.False(Permissions.TenantDeactivate.Delegable);
        Assert.False(Delegation.CanDelegate(owner, Permissions.TenantDeactivate.Key));
    }

    [Fact]
    public void Allowed_Restricts_A_Desired_Set_To_The_Delegable_Ceiling()
    {
        var owner = RoleGrants.BaselinePermissions(Roles.Owner);
        var desired = new[]
        {
            Permissions.FleetGatewayEnroll.Key,   // delegable + held → kept
            Permissions.TenantDeactivate.Key,     // non-delegable → dropped
            "fleet.gateway.obliterate",           // unknown → dropped
        };
        var allowed = Delegation.Allowed(owner, desired);
        Assert.Equal(new HashSet<string> { Permissions.FleetGatewayEnroll.Key }, allowed);
    }
}
