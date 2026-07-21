using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Scoped role assignment resolution (P3, ADR 0020 §2): a subject's effective
/// roles at a target scope are the union of every active assignment whose scope
/// contains the target — so a grant cascades down the tree, a tenant-root grant is
/// tenant-wide (today's behaviour), and expired grants drop out.
/// </summary>
public sealed class RoleAssignmentTests
{
    private const string Ten = "ten_1";
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static ScopeNode Node(string id, ScopeType type, string? parent) => new(id, Ten, type, id, parent);

    // root ─ site ─ lab ─ dept ; plus lab2 directly under root.
    private static readonly ScopeTree Tree = ScopeTree.Build(
    [
        Node("root", ScopeType.Tenant, null),
        Node("site", ScopeType.Site, "root"),
        Node("lab", ScopeType.Laboratory, "site"),
        Node("dept", ScopeType.Department, "lab"),
        Node("lab2", ScopeType.Laboratory, "root"),
    ]);

    private static RoleAssignment A(string id, string user, string role, string scope, DateTimeOffset? expires = null) =>
        new(id, user, role, scope, expires);

    [Fact]
    public void A_Grant_Cascades_To_Descendant_Scopes()
    {
        var assignments = new[] { A("a1", "u1", "lab-admin", "site") };
        Assert.Contains("lab-admin", RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "dept", Now));
        Assert.Contains("lab-admin", RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "lab", Now));
        // ...but not to a sibling lab that hangs off the root, not the site.
        Assert.Empty(RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "lab2", Now));
    }

    [Fact]
    public void A_Tenant_Root_Grant_Is_Tenant_Wide()
    {
        var assignments = new[] { A("a1", "u1", "owner", "root") };
        foreach (var scope in new[] { "root", "site", "lab", "dept", "lab2" })
        {
            Assert.Contains("owner", RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", scope, Now));
        }
    }

    [Fact]
    public void Effective_Roles_Are_The_Union_Across_Assignments()
    {
        var assignments = new[]
        {
            A("a1", "u1", "read-only", "root"),
            A("a2", "u1", "lab-admin", "lab"),
            A("a3", "u2", "owner", "root"), // another user — must not leak in
        };
        var atDept = RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "dept", Now);
        Assert.Equal(new HashSet<string> { "read-only", "lab-admin" }, atDept);
        // At lab2, only the root-level read-only applies.
        Assert.Equal(new HashSet<string> { "read-only" }, RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "lab2", Now));
    }

    [Fact]
    public void Expired_Assignments_Are_Excluded()
    {
        var expired = A("a1", "u1", "lab-admin", "lab", Now.AddMinutes(-1));
        var live = A("a2", "u1", "technician", "lab", Now.AddMinutes(1));
        var roles = RoleAssignments.EffectiveRolesAt([expired, live], Tree, "u1", "dept", Now);
        Assert.Equal(new HashSet<string> { "technician" }, roles);

        Assert.False(RoleAssignments.IsActiveAt(expired, Now));
        Assert.True(RoleAssignments.IsActiveAt(live, Now));
        Assert.True(RoleAssignments.IsActiveAt(A("a3", "u1", "r", "lab", expires: null), Now)); // no expiry
    }

    [Fact]
    public void A_Deeper_Grant_Does_Not_Cover_Ancestor_Scopes()
    {
        var assignments = new[] { A("a1", "u1", "technician", "dept") };
        Assert.Contains("technician", RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "dept", Now));
        Assert.Empty(RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "lab", Now));  // up the tree
        Assert.Empty(RoleAssignments.EffectiveRolesAt(assignments, Tree, "u1", "root", Now));
    }
}
