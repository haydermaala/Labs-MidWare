using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>
/// The P3 scope hierarchy (tenant → site → laboratory → department): tree
/// validation and the containment relation that scoped authorization is defined
/// against — a grant at a scope covers that scope and everything below it.
/// </summary>
public sealed class ScopeTreeTests
{
    private const string T = "ten_1";

    private static ScopeNode N(string id, ScopeType type, string? parent) => new(id, T, type, id, parent);

    // tenant ─ site ─ lab ─ dept, plus a lab hung directly off the tenant (no site).
    private static ScopeTree Sample() => ScopeTree.Build(
    [
        N("root", ScopeType.Tenant, null),
        N("site", ScopeType.Site, "root"),
        N("lab", ScopeType.Laboratory, "site"),
        N("dept", ScopeType.Department, "lab"),
        N("lab2", ScopeType.Laboratory, "root"),
    ]);

    [Fact]
    public void Build_Accepts_A_Valid_Tree_Including_Skipped_Levels()
    {
        var tree = Sample();
        Assert.Equal(T, tree.TenantId);
        Assert.Equal("root", tree.Root.Id);
    }

    [Fact]
    public void Contains_Is_Reflexive_And_Follows_Ancestry()
    {
        var tree = Sample();
        Assert.True(tree.Contains("root", "root"));   // reflexive
        Assert.True(tree.Contains("root", "dept"));   // root covers everything
        Assert.True(tree.Contains("site", "dept"));   // site covers its lab's dept
        Assert.True(tree.Contains("lab", "dept"));
        Assert.False(tree.Contains("dept", "lab"));   // a child does not cover its parent
        Assert.False(tree.Contains("site", "lab2"));  // lab2 hangs off root, not site
    }

    [Fact]
    public void AncestorsAndSelf_And_Path_Run_Root_To_Leaf()
    {
        var tree = Sample();
        Assert.Equal(["dept", "lab", "site", "root"], tree.AncestorsAndSelf("dept").Select(n => n.Id));
        Assert.Equal("/root/site/lab/dept", tree.PathOf("dept"));
        Assert.Equal("/root/lab2", tree.PathOf("lab2"));
    }

    [Fact]
    public void DescendantsAndSelf_Returns_The_Subtree()
    {
        var tree = Sample();
        Assert.Equal(["dept", "lab", "site"], tree.DescendantsAndSelf("site").Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(["dept"], tree.DescendantsAndSelf("dept").Select(n => n.Id));
    }

    [Fact]
    public void Build_Rejects_Missing_Or_Multiple_Roots()
    {
        Assert.Throws<ArgumentException>(() => ScopeTree.Build([N("site", ScopeType.Site, null)])); // root not a Tenant
        Assert.Throws<ArgumentException>(() => ScopeTree.Build(
        [
            N("root", ScopeType.Tenant, null),
            N("root2", ScopeType.Tenant, null), // two roots
        ]));
    }

    [Fact]
    public void Build_Rejects_Invalid_Nesting_And_Unknown_Parents()
    {
        // A site cannot contain a tenant (parent must be strictly shallower).
        Assert.Throws<ArgumentException>(() => ScopeTree.Build(
        [
            N("root", ScopeType.Tenant, null),
            N("site", ScopeType.Site, "root"),
            N("bad", ScopeType.Tenant, "site"),
        ]));
        // Unknown parent id.
        Assert.Throws<ArgumentException>(() => ScopeTree.Build(
        [
            N("root", ScopeType.Tenant, null),
            N("lab", ScopeType.Laboratory, "ghost"),
        ]));
    }

    [Fact]
    public void CanContain_Is_Strictly_Shallower()
    {
        Assert.True(Scopes.CanContain(ScopeType.Tenant, ScopeType.Department));
        Assert.True(Scopes.CanContain(ScopeType.Site, ScopeType.Laboratory));
        Assert.False(Scopes.CanContain(ScopeType.Laboratory, ScopeType.Laboratory)); // same level
        Assert.False(Scopes.CanContain(ScopeType.Department, ScopeType.Site));        // deeper→shallower
    }
}
