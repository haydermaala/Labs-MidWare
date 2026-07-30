using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Scope-aware authorization (P3, ADR 0020 §4): the engine resolves the subject's
/// effective roles at the target scope (via role assignments + the scope tree),
/// then applies the same P2 permission/step-up checks — so a grant cascades down
/// the hierarchy and a tenant-root grant behaves exactly as today.
/// </summary>
public sealed class ScopedAuthorizationTests
{
    private const string Ten = "ten_1";
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly ScopedAuthorizationEngine Engine = new(new AuthorizationEngine());

    private static ScopeNode Node(string id, ScopeType type, string? parent) => new(id, Ten, type, id, parent);

    // root ─ site ─ lab ─ dept
    private static readonly ScopeTree Tree = ScopeTree.Build(
    [
        Node("root", ScopeType.Tenant, null),
        Node("site", ScopeType.Site, "root"),
        Node("lab", ScopeType.Laboratory, "site"),
        Node("dept", ScopeType.Department, "lab"),
    ]);

    private static ScopedAuthorizationRequest Req(
        IReadOnlyCollection<RoleAssignment> assignments, string userId, string scope, string permission,
        bool fresh = true) =>
        new(assignments, Tree, userId, scope, permission, Now, MfaSatisfied: true, FreshAuth: fresh, ApprovalGranted: true);

    [Fact]
    public void A_Role_Assigned_At_A_Scope_Grants_Its_Permissions_On_Descendants()
    {
        var assignments = new[] { new RoleAssignment("a1", "u1", Roles.LabAdmin, "site", null) };
        // lab-admin can manage the fleet at a department beneath the assigned site.
        Assert.True(Engine.Authorize(Req(assignments, "u1", "dept", Permissions.FleetGatewayEnroll.Key)).IsAllowed);
        // ...but a technician-only capability check still applies: lab-admin cannot manage users.
        Assert.False(Engine.Authorize(Req(assignments, "u1", "dept", Permissions.MembersMemberInvite.Key)).IsAllowed);
    }

    [Fact]
    public void No_Assignment_Reaching_The_Scope_Is_Denied()
    {
        var assignments = new[] { new RoleAssignment("a1", "u1", Roles.Owner, "lab", null) };
        // An owner at 'lab' does not reach a sibling path; and nothing reaches 'site' (an ancestor).
        var atSite = Engine.Authorize(Req(assignments, "u1", "site", Permissions.FleetGatewayView.Key));
        Assert.False(atSite.IsAllowed);
        Assert.Contains("no active role", atSite.Reason);
    }

    [Fact]
    public void Tenant_Root_Assignment_Behaves_Like_Todays_Tenant_Wide_Role()
    {
        var assignments = new[] { new RoleAssignment("a1", "u1", Roles.Owner, "root", null) };
        foreach (var scope in new[] { "root", "site", "lab", "dept" })
        {
            Assert.True(Engine.Authorize(Req(assignments, "u1", scope, Permissions.TenantRename.Key)).IsAllowed);
        }
    }

    [Fact]
    public void Unknown_Scope_Is_Denied()
    {
        var assignments = new[] { new RoleAssignment("a1", "u1", Roles.Owner, "root", null) };
        var result = Engine.Authorize(Req(assignments, "u1", "ghost", Permissions.FleetGatewayView.Key));
        Assert.False(result.IsAllowed);
        Assert.Contains("unknown scope", result.Reason);
    }

    [Fact]
    public void Step_Up_Still_Applies_On_Top_Of_The_Scope_Resolution()
    {
        var assignments = new[] { new RoleAssignment("a1", "u1", Roles.Owner, "root", null) };
        // decommission is RequiresFreshAuth: granted by role + scope, but denied without fresh auth.
        var stale = Engine.Authorize(Req(assignments, "u1", "dept", Permissions.FleetGatewayDecommission.Key, fresh: false));
        Assert.False(stale.IsAllowed);
        Assert.True(stale.RequiresStepUp);
    }

    [Fact]
    public void Expired_Assignment_Does_Not_Grant()
    {
        var assignments = new[] { new RoleAssignment("a1", "u1", Roles.Owner, "root", Now.AddMinutes(-1)) };
        Assert.False(Engine.Authorize(Req(assignments, "u1", "dept", Permissions.FleetGatewayView.Key)).IsAllowed);
    }
}
