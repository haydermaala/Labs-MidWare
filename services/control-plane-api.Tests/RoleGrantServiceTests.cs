using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>RoleGrantService authors P3 scoped role assignments and custom roles,
/// enforcing the two model invariants on every write: delegation limits (you may
/// only hand out delegable permissions you hold) and separation of duty (a grant
/// must not leave the target holding both sides of a rule).</summary>
public sealed class RoleGrantServiceTests
{
    private sealed class Factory(string name) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    private static (RoleGrantService Svc, Factory F) New()
    {
        var f = new Factory($"grants_{Guid.NewGuid():N}");
        return (new RoleGrantService(f, TimeProvider.System), f);
    }

    private static string SeedScope(Factory f, string tenantId = "ten_1")
    {
        using var db = f.CreateDbContext();
        var scope = new ScopeEntity
        {
            Id = Ids.New("scp"),
            TenantId = tenantId,
            Type = ScopeType.Tenant.ToString(),
            Name = "root",
            Path = "/root",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Scopes.Add(scope);
        db.SaveChanges();
        return scope.Id;
    }

    private static void SeedSodRule(Factory f, string a, string b, string tenantId = "ten_1")
    {
        using var db = f.CreateDbContext();
        db.SodRules.Add(new SodRuleEntity
        {
            Id = Ids.New("sod"),
            TenantId = tenantId,
            Name = "author-not-approver",
            PermissionA = a,
            PermissionB = b,
            Active = true,
        });
        db.SaveChanges();
    }

    [Fact]
    public void Grant_Succeeds_And_Is_Listed()
    {
        var (svc, f) = New();
        var scope = SeedScope(f);

        var r = svc.Grant("ten_1", "u_owner", Roles.Owner, "u_target", Roles.ReadOnly, scope, expiresAt: null);

        Assert.Equal(RoleGrantService.GrantOutcome.Ok, r.Outcome);
        Assert.NotNull(r.Assignment);
        var listed = svc.AssignmentsFor("ten_1", "u_target");
        Assert.Single(listed);
        Assert.Equal(Roles.ReadOnly, listed[0].Role);
        Assert.True(listed[0].Active);
    }

    [Fact]
    public void Grant_Rejects_Unknown_Scope_And_Role()
    {
        var (svc, f) = New();
        var scope = SeedScope(f);

        Assert.Equal(RoleGrantService.GrantOutcome.UnknownScope,
            svc.Grant("ten_1", "u_o", Roles.Owner, "u_t", Roles.ReadOnly, "scp_ghost", null).Outcome);
        Assert.Equal(RoleGrantService.GrantOutcome.UnknownRole,
            svc.Grant("ten_1", "u_o", Roles.Owner, "u_t", "not-a-role", scope, null).Outcome);
        // A scope in another tenant is not usable here.
        var otherScope = SeedScope(f, "ten_2");
        Assert.Equal(RoleGrantService.GrantOutcome.UnknownScope,
            svc.Grant("ten_1", "u_o", Roles.Owner, "u_t", Roles.ReadOnly, otherScope, null).Outcome);
    }

    [Fact]
    public void Grant_Denied_When_Role_Carries_A_NonDelegable_Permission()
    {
        var (svc, f) = New();
        var scope = SeedScope(f);

        // The Owner role includes tenant.tenant.deactivate, which is not delegable —
        // so even an owner cannot scope-grant full ownership.
        var r = svc.Grant("ten_1", "u_owner", Roles.Owner, "u_target", Roles.Owner, scope, null);

        Assert.Equal(RoleGrantService.GrantOutcome.DelegationDenied, r.Outcome);
        Assert.Contains(Permissions.TenantDeactivate.Key, r.Offending);
    }

    [Fact]
    public void Grant_Denied_When_Grantor_Lacks_The_Permissions()
    {
        var (svc, f) = New();
        var scope = SeedScope(f);

        // A lab-admin (View + ManageFleet) cannot grant tenant-admin (needs the
        // members.* / ManageUsers permissions it does not hold).
        var r = svc.Grant("ten_1", "u_lab", Roles.LabAdmin, "u_target", Roles.TenantAdmin, scope, null);

        Assert.Equal(RoleGrantService.GrantOutcome.DelegationDenied, r.Outcome);
        Assert.Contains(Permissions.MembersMemberChangeRole.Key, r.Offending);
    }

    [Fact]
    public void Grant_Blocked_By_Separation_Of_Duty()
    {
        var (svc, f) = New();
        var scope = SeedScope(f);
        // No single subject may both change roles and remove members.
        SeedSodRule(f, Permissions.MembersMemberChangeRole.Key, Permissions.MembersMemberRemove.Key);

        // tenant-admin holds both sides → the grant would violate the rule.
        var r = svc.Grant("ten_1", "u_owner", Roles.Owner, "u_target", Roles.TenantAdmin, scope, null);

        Assert.Equal(RoleGrantService.GrantOutcome.SodViolation, r.Outcome);
        Assert.Contains("author-not-approver", r.Offending);
    }

    [Fact]
    public void Revoke_Is_Idempotent_And_Deactivates()
    {
        var (svc, f) = New();
        var scope = SeedScope(f);
        var granted = svc.Grant("ten_1", "u_owner", Roles.Owner, "u_target", Roles.ReadOnly, scope, null);
        var id = granted.Assignment!.Id;

        Assert.True(svc.Revoke("ten_1", id));
        Assert.False(svc.Revoke("ten_1", id));           // already revoked
        Assert.False(svc.Revoke("ten_1", "rga_ghost"));  // unknown
        Assert.False(svc.AssignmentsFor("ten_1", "u_target")[0].Active);
    }

    [Fact]
    public void CreateCustomRole_Succeeds_And_Grant_Recognizes_It()
    {
        var (svc, f) = New();
        var scope = SeedScope(f);

        var created = svc.CreateCustomRole("ten_1", "u_owner", Roles.Owner, "fleet-viewer", "Fleet Viewer",
            [Permissions.FleetGatewayView.Key, Permissions.FleetConfigView.Key]);

        Assert.Equal(RoleGrantService.CustomRoleOutcome.Ok, created.Outcome);
        Assert.Equal(2, created.Role!.PermissionKeys.Count);
        Assert.Single(svc.CustomRolesFor("ten_1"));

        // The custom role is now a known, grantable role.
        var g = svc.Grant("ten_1", "u_owner", Roles.Owner, "u_target", "fleet-viewer", scope, null);
        Assert.Equal(RoleGrantService.GrantOutcome.Ok, g.Outcome);
    }

    [Fact]
    public void CreateCustomRole_Rejects_Reserved_Key_Duplicate_And_NonDelegable()
    {
        var (svc, _) = New();

        Assert.Equal(RoleGrantService.CustomRoleOutcome.ReservedRoleKey,
            svc.CreateCustomRole("ten_1", "u", Roles.Owner, Roles.Owner, "x", [Permissions.FleetGatewayView.Key]).Outcome);

        Assert.Equal(RoleGrantService.CustomRoleOutcome.NoValidPermissions,
            svc.CreateCustomRole("ten_1", "u", Roles.Owner, "empty", "x", ["not.a.permission"]).Outcome);

        Assert.Equal(RoleGrantService.CustomRoleOutcome.DelegationDenied,
            svc.CreateCustomRole("ten_1", "u", Roles.Owner, "danger", "x", [Permissions.TenantDeactivate.Key]).Outcome);

        Assert.Equal(RoleGrantService.CustomRoleOutcome.Ok,
            svc.CreateCustomRole("ten_1", "u", Roles.Owner, "dup", "x", [Permissions.FleetGatewayView.Key]).Outcome);
        Assert.Equal(RoleGrantService.CustomRoleOutcome.RoleKeyTaken,
            svc.CreateCustomRole("ten_1", "u", Roles.Owner, "dup", "x", [Permissions.FleetGatewayView.Key]).Outcome);
    }
}
