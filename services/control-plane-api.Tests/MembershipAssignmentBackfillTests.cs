using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>The P3 backfill mirrors each active membership to a role assignment at
/// the tenant root, so the scope-aware engine can be adopted without any member
/// losing access. It is idempotent and seeds at most one root assignment per
/// (tenant, user), so it never duplicates or fights an explicit/revoked grant.</summary>
public sealed class MembershipAssignmentBackfillTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"backfill_{Guid.NewGuid():N}").Options);

    private static void SeedMember(AppDbContext db, string tenantId, string userId, string role, bool active = true)
    {
        if (!db.Tenants.Any(t => t.Id == tenantId))
        {
            db.Tenants.Add(new TenantEntity { Id = tenantId, Name = tenantId + " Labs", CreatedAt = DateTimeOffset.UtcNow });
        }
        db.Memberships.Add(new MembershipEntity
        {
            Id = Ids.New("mem"),
            TenantId = tenantId,
            UserId = userId,
            Role = role,
            Active = active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public void Mirrors_Active_Membership_To_A_Root_Assignment()
    {
        using var db = NewDb();
        SeedMember(db, "ten_1", "u_1", Roles.Owner);

        var created = MembershipAssignmentBackfill.Apply(db, TimeProvider.System);

        Assert.Equal(1, created);
        var root = db.Scopes.Single(s => s.TenantId == "ten_1" && s.ParentId == null);
        Assert.Equal(ScopeType.Tenant.ToString(), root.Type);
        var a = db.RoleAssignments.Single(x => x.TenantId == "ten_1" && x.UserId == "u_1");
        Assert.Equal(Roles.Owner, a.Role);
        Assert.Equal(root.Id, a.ScopeId);
        Assert.Equal("backfill", a.GrantedByUserId);
        Assert.Null(a.RevokedAt);
    }

    [Fact]
    public void Is_Idempotent()
    {
        using var db = NewDb();
        SeedMember(db, "ten_1", "u_1", Roles.Owner);
        SeedMember(db, "ten_1", "u_2", Roles.Technician);

        Assert.Equal(2, MembershipAssignmentBackfill.Apply(db, TimeProvider.System));
        Assert.Equal(0, MembershipAssignmentBackfill.Apply(db, TimeProvider.System)); // second run: nothing new
        Assert.Single(db.Scopes.Where(s => s.ParentId == null)); // exactly one root, not re-created
        Assert.Equal(2, db.RoleAssignments.Count());
    }

    [Fact]
    public void Skips_Inactive_Memberships()
    {
        using var db = NewDb();
        SeedMember(db, "ten_1", "u_active", Roles.Owner);
        SeedMember(db, "ten_1", "u_gone", Roles.ReadOnly, active: false);

        Assert.Equal(1, MembershipAssignmentBackfill.Apply(db, TimeProvider.System));
        Assert.DoesNotContain(db.RoleAssignments, a => a.UserId == "u_gone");
    }

    [Fact]
    public void Reuses_An_Existing_Root_Scope_And_Skips_Users_With_A_Root_Assignment()
    {
        using var db = NewDb();
        SeedMember(db, "ten_1", "u_1", Roles.Owner);
        // A root scope already built via the scope API, plus an explicit root grant for u_1.
        var root = new ScopeEntity
        {
            Id = Ids.New("scp"), TenantId = "ten_1", Type = ScopeType.Tenant.ToString(),
            Name = "HQ", ParentId = null, Path = "/hq", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Scopes.Add(root);
        db.RoleAssignments.Add(new RoleAssignmentEntity
        {
            Id = Ids.New("rga"), TenantId = "ten_1", UserId = "u_1", Role = Roles.Auditor,
            ScopeId = root.Id, GrantedByUserId = "u_admin", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var created = MembershipAssignmentBackfill.Apply(db, TimeProvider.System);

        Assert.Equal(0, created); // u_1 already has a root assignment — not re-seeded
        Assert.Single(db.Scopes.Where(s => s.ParentId == null)); // reused the existing root
        Assert.Single(db.RoleAssignments); // still just the explicit Auditor grant
    }

    [Fact]
    public void Does_Not_Recreate_A_Revoked_Backfill()
    {
        using var db = NewDb();
        SeedMember(db, "ten_1", "u_1", Roles.Owner);
        MembershipAssignmentBackfill.Apply(db, TimeProvider.System);

        // An admin later revokes the backfilled root assignment.
        var a = db.RoleAssignments.Single();
        a.RevokedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();

        Assert.Equal(0, MembershipAssignmentBackfill.Apply(db, TimeProvider.System)); // not undone
        Assert.NotNull(db.RoleAssignments.Single().RevokedAt);
    }
}
