using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>ScopeService persists a tenant's org hierarchy: an idempotent root,
/// validated child creation with a maintained path, and reads back as a ScopeTree.</summary>
public sealed class ScopeServiceTests
{
    private sealed class Factory(string name) : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options;

        public AppDbContext CreateDbContext() => new(_options);
    }

    private static ScopeService New() =>
        new(new Factory($"scope_{Guid.NewGuid():N}"), TimeProvider.System);

    [Fact]
    public void EnsureRoot_Is_Idempotent()
    {
        var svc = New();
        var a = svc.EnsureRoot("ten_1", "Acme Labs");
        var b = svc.EnsureRoot("ten_1", "Acme Labs");
        Assert.Equal(a.Id, b.Id);
        Assert.Null(a.ParentId);
        Assert.Equal(ScopeType.Tenant.ToString(), a.Type);
        Assert.Equal("/" + a.Id, a.Path);
    }

    [Fact]
    public void CreateChild_Validates_Nesting_And_Maintains_Path()
    {
        var svc = New();
        var root = svc.EnsureRoot("ten_1", "Acme");
        var site = svc.CreateChild("ten_1", root.Id, ScopeType.Site, "North Site");
        Assert.NotNull(site);
        Assert.Equal($"/{root.Id}/{site!.Id}", site.Path);

        var lab = svc.CreateChild("ten_1", site.Id, ScopeType.Laboratory, "Haematology");
        Assert.NotNull(lab);
        Assert.Equal($"/{root.Id}/{site.Id}/{lab!.Id}", lab.Path);

        // Invalid: a site cannot contain a tenant (parent must be strictly shallower).
        Assert.Null(svc.CreateChild("ten_1", site.Id, ScopeType.Tenant, "bad"));
        // Unknown parent.
        Assert.Null(svc.CreateChild("ten_1", "scp_ghost", ScopeType.Site, "bad"));
    }

    [Fact]
    public void Tree_Builds_And_Contains_Follows_Ancestry()
    {
        var svc = New();
        var root = svc.EnsureRoot("ten_1", "Acme");
        var site = svc.CreateChild("ten_1", root.Id, ScopeType.Site, "S")!;
        var lab = svc.CreateChild("ten_1", site.Id, ScopeType.Laboratory, "L")!;

        var tree = svc.Tree("ten_1");
        Assert.NotNull(tree);
        Assert.Equal(root.Id, tree!.Root.Id);
        Assert.True(tree.Contains(root.Id, lab.Id));   // root covers the lab
        Assert.True(tree.Contains(site.Id, lab.Id));
        Assert.False(tree.Contains(lab.Id, site.Id));  // not upward
    }

    [Fact]
    public void Tree_Is_Null_Before_A_Root_Exists()
    {
        Assert.Null(New().Tree("ten_unknown"));
    }

    [Fact]
    public void Scopes_Are_Isolated_Per_Tenant()
    {
        var svc = New();
        var rootA = svc.EnsureRoot("ten_a", "A");
        svc.EnsureRoot("ten_b", "B");
        // A child under tenant A's root is not creatable from tenant B's context.
        Assert.Null(svc.CreateChild("ten_b", rootA.Id, ScopeType.Site, "x"));
        Assert.Single(svc.Tree("ten_a")!.DescendantsAndSelf(rootA.Id)); // just the root
    }
}
