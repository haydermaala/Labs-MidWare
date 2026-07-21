using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>
/// P2 permission catalog + engine. The load-bearing test is <see
/// cref="Role_Matrix_Exactly_Reproduces_Legacy_Capability_Checks"/>: it proves the
/// new data-driven matrix grants precisely what today's <c>Roles.Can*</c> checks
/// grant, so introducing the engine changes no one's access. The rest cover the
/// catalog's integrity and the engine's deny-by-default + step-up behaviour.
/// </summary>
public sealed class PermissionEngineTests
{
    private static readonly AuthorizationEngine Engine = new();

    private static AuthorizationRequest Request(
        string role, string permissionKey, bool mfa = true, bool fresh = true, bool approval = true) =>
        new([role], permissionKey, mfa, fresh, approval);

    // The legacy predicate for a permission's capability — the behaviour P2 must keep.
    private static bool LegacyGrants(string role, PermissionDefinition p) => p.Capability switch
    {
        LegacyCapability.View => Roles.CanView(role),
        LegacyCapability.ManageFleet => Roles.CanManageFleet(role),
        LegacyCapability.ManageUsers => Roles.CanManageUsers(role),
        LegacyCapability.ManageTenant => Roles.CanManageTenant(role),
        LegacyCapability.ManageBilling => Roles.CanManageBilling(role),
        _ => false,
    };

    [Fact]
    public void Catalog_Keys_Are_Unique_And_Well_Formed()
    {
        Assert.NotEmpty(Permissions.All);
        Assert.Equal(Permissions.All.Count, Permissions.All.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count());

        foreach (var p in Permissions.All)
        {
            Assert.Equal($"{p.Domain}.{p.Resource}.{p.Action}", p.Key);
            Assert.False(string.IsNullOrWhiteSpace(p.Description), $"{p.Key} needs a description");
            Assert.Same(p, Permissions.Find(p.Key));
        }
    }

    [Fact]
    public void Role_Matrix_Exactly_Reproduces_Legacy_Capability_Checks()
    {
        foreach (var role in Roles.All)
        {
            foreach (var p in Permissions.All)
            {
                Assert.Equal(LegacyGrants(role, p), RolePermissions.Grants(role, p));
            }
        }
    }

    [Fact]
    public void Engine_Decisions_Match_The_Legacy_Matrix_When_Requirements_Are_Met()
    {
        foreach (var role in Roles.All)
        {
            foreach (var p in Permissions.All)
            {
                var result = Engine.Authorize(Request(role, p.Key));
                Assert.Equal(LegacyGrants(role, p), result.IsAllowed);
                Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            }
        }
    }

    [Fact]
    public void Unknown_Permission_Is_Denied()
    {
        var result = Engine.Authorize(Request(Roles.Owner, "fleet.gateway.obliterate"));
        Assert.False(result.IsAllowed);
        Assert.Contains("unknown permission", result.Reason);
    }

    [Fact]
    public void No_Roles_Is_Denied()
    {
        var result = Engine.Authorize(new AuthorizationRequest([], Permissions.FleetGatewayView.Key));
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void A_Role_Without_The_Capability_Is_Denied_With_Reason()
    {
        // read-only may view but not enroll.
        var allowed = Engine.Authorize(Request(Roles.ReadOnly, Permissions.FleetGatewayView.Key));
        var denied = Engine.Authorize(Request(Roles.ReadOnly, Permissions.FleetGatewayEnroll.Key));
        Assert.True(allowed.IsAllowed);
        Assert.False(denied.IsAllowed);
        Assert.Contains("no role", denied.Reason);
    }

    [Fact]
    public void Step_Up_Permission_Requires_Fresh_Auth_Even_For_A_Granting_Role()
    {
        var key = Permissions.FleetGatewayDecommission.Key; // RequiresFreshAuth, granted to Owner
        Assert.True(Permissions.FleetGatewayDecommission.RequiresFreshAuth);

        var withoutStepUp = Engine.Authorize(Request(Roles.Owner, key, fresh: false));
        var withStepUp = Engine.Authorize(Request(Roles.Owner, key, fresh: true));

        Assert.False(withoutStepUp.IsAllowed);
        Assert.True(withoutStepUp.RequiresStepUp); // satisfiable by re-auth (drives the UI prompt)
        Assert.Contains("re-authentication", withoutStepUp.Reason);
        Assert.True(withStepUp.IsAllowed);
        Assert.False(withStepUp.RequiresStepUp);
    }

    [Fact]
    public void Owner_Holds_Every_Capability_And_ReadOnly_Holds_Only_View()
    {
        var ownerCaps = RolePermissions.GrantedTo(Roles.Owner).Select(p => p.Capability).ToHashSet();
        Assert.Equal(Enum.GetValues<LegacyCapability>().ToHashSet(), ownerCaps);

        var readOnlyCaps = RolePermissions.GrantedTo(Roles.ReadOnly).Select(p => p.Capability).Distinct();
        Assert.Equal([LegacyCapability.View], readOnlyCaps);
    }
}
