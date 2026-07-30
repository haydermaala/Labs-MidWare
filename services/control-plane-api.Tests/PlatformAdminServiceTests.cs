using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>P6 platform role-assignment persistence: grant/revoke platform (super-admin)
/// roles and resolve a user's active roles for the platform authorization engine.
/// Global and disjoint from tenant membership.</summary>
public sealed class PlatformAdminServiceTests
{
    private sealed class Factory(string name) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    private static PlatformAdminService New() =>
        new(new Factory($"platform_{Guid.NewGuid():N}"), TimeProvider.System);

    [Fact]
    public void Grant_Assigns_A_Known_Role_And_RolesFor_Resolves_It()
    {
        var svc = New();
        var r = svc.Grant("root_user", "u_ops", PlatformRoles.OperationsAdmin, expiresAt: null, reason: null);

        Assert.Equal(PlatformAdminService.GrantOutcome.Ok, r.Outcome);
        Assert.True(r.Assignment!.Active);
        Assert.Equal([PlatformRoles.OperationsAdmin], svc.RolesFor("u_ops"));
        // A user with no platform grant holds nothing.
        Assert.Empty(svc.RolesFor("u_nobody"));
    }

    [Fact]
    public void Grant_Rejects_An_Unknown_Role()
    {
        var svc = New();
        Assert.Equal(PlatformAdminService.GrantOutcome.UnknownRole,
            svc.Grant("g", "u", "not-a-platform-role", null, null).Outcome);
        // A TENANT role is not a platform role.
        Assert.Equal(PlatformAdminService.GrantOutcome.UnknownRole,
            svc.Grant("g", "u", Roles.Owner, null, null).Outcome);
    }

    [Fact]
    public void Revoke_Removes_The_Role_And_Is_Idempotent()
    {
        var svc = New();
        var id = svc.Grant("g", "u", PlatformRoles.Auditor, null, null).Assignment!.Id;

        Assert.True(svc.Revoke(id));
        Assert.Empty(svc.RolesFor("u"));
        Assert.False(svc.Revoke(id));            // already revoked
        Assert.False(svc.Revoke("pra_ghost"));   // unknown
    }

    [Fact]
    public void Expired_Assignments_Are_Not_Active()
    {
        var svc = New();
        svc.Grant("g", "u", PlatformRoles.SupportEngineer,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1), reason: null);
        // Time-bounded grant already elapsed → not resolved.
        Assert.Empty(svc.RolesFor("u"));
        Assert.False(svc.Assignments("u").Single().Active);
    }

    [Fact]
    public void RolesFor_Unions_Multiple_Active_Grants()
    {
        var svc = New();
        svc.Grant("g", "u", PlatformRoles.BillingAdmin, null, null);
        svc.Grant("g", "u", PlatformRoles.Auditor, null, null);
        var roles = svc.RolesFor("u");
        Assert.Contains(PlatformRoles.BillingAdmin, roles);
        Assert.Contains(PlatformRoles.Auditor, roles);
    }

    [Fact]
    public void Assignments_Filters_By_User()
    {
        var svc = New();
        svc.Grant("g", "u_a", PlatformRoles.Auditor, null, "audit season");
        svc.Grant("g", "u_b", PlatformRoles.ReleaseManager, null, null);
        Assert.Single(svc.Assignments("u_a"));
        Assert.Equal("audit season", svc.Assignments("u_a").Single().Reason);
        Assert.Equal(2, svc.Assignments(null).Count);
    }
}
