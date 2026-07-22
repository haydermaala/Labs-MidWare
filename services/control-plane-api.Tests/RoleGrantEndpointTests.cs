using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>HTTP surface for P3 scoped grants + custom roles: the member-admin
/// permission gate, anti-enumeration status split, and delegation/SoD outcomes
/// mapping to the right status codes.</summary>
public sealed class RoleGrantEndpointTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;

    private static readonly string[] FleetViewPerm = ["fleet.gateway.view"];
    private static readonly string[] TenantDeactivatePerm = ["tenant.tenant.deactivate"];
    private static readonly string[] FleetEnrollPerm = ["fleet.gateway.enroll"];

    public RoleGrantEndpointTests(EmailApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record AssignmentDto(string Id, string UserId, string Role, string ScopeId, bool Active);
    private sealed record CustomRoleDto(string RoleKey, string Name, List<string> PermissionKeys);
    private sealed record ScopeDto(string Id, string Type, string Name, string? ParentId, string Path);
    private sealed record BootstrapDto(string Token, DateTimeOffset ExpiresAt);
    private sealed record EnrollDto(string GatewayId, string TenantId, string DeviceCredential);

    private HttpClient Admin()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return c;
    }

    private HttpClient Session(string token)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private async Task<string> NewTenant(string name) =>
        (await (await Admin().PostAsJsonAsync("/api/tenants", new { name }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;

    private async Task<(string UserId, string Session)> NewUser(string email)
    {
        var created = await Admin().PostAsJsonAsync("/api/admin/users",
            new { email, password = "correct horse battery staple" });
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        return (user.Id, (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken);
    }

    private async Task GrantMembership(string userId, string tenantId, string role) =>
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId, tenantId, role })).StatusCode);

    // Build the tenant-root scope through the real endpoint (admin bypasses the gate).
    private async Task<string> SeedScope(string tenantId) =>
        (await (await Admin().PostAsJsonAsync($"/api/tenants/{tenantId}/scopes", new { name = "root" }))
            .Content.ReadFromJsonAsync<ScopeDto>())!.Id;

    private static async Task<ScopeDto> Child(HttpClient owner, string tenant, string parentId, string type, string name) =>
        (await (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes",
            new { parentId, type, name })).Content.ReadFromJsonAsync<ScopeDto>())!;

    private async Task<string> EnrollGateway(HttpClient owner, string tenant, string name)
    {
        var token = (await (await owner.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null))
            .Content.ReadFromJsonAsync<BootstrapDto>())!;
        var enrolled = (await (await _factory.CreateClient().PostAsJsonAsync("/api/gateways/enroll",
            new { bootstrapToken = token.Token, name })).Content.ReadFromJsonAsync<EnrollDto>())!;
        return enrolled.GatewayId;
    }

    [Fact]
    public async Task Owner_Grants_Scoped_Role_Nonmember_Gets_401()
    {
        var tenant = await NewTenant("Grant Lab");
        var (ownerId, ownerSession) = await NewUser("grant-owner@example.test");
        await GrantMembership(ownerId, tenant, "owner");
        var (targetId, _) = await NewUser("grant-target@example.test");
        var scope = await SeedScope(tenant);

        var res = await Session(ownerSession).PostAsJsonAsync($"/api/tenants/{tenant}/role-assignments",
            new { userId = targetId, role = "read-only", scopeId = scope });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var list = await Session(ownerSession)
            .GetFromJsonAsync<List<AssignmentDto>>($"/api/tenants/{tenant}/role-assignments?userId={targetId}");
        Assert.Single(list!);
        Assert.True(list![0].Active);

        // A non-member gets 401 (indistinguishable from no-such-tenant), never 403.
        var (_, strangerSession) = await NewUser("grant-stranger@example.test");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(strangerSession).GetAsync($"/api/tenants/{tenant}/role-assignments")).StatusCode);
    }

    [Fact]
    public async Task Scope_Hierarchy_Is_Built_Read_And_Gated()
    {
        var tenant = await NewTenant("Scope Lab");
        var (ownerId, ownerSession) = await NewUser("scope-owner@example.test");
        await GrantMembership(ownerId, tenant, "owner");
        var owner = Session(ownerSession);

        var root = (await (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes", new { name = "HQ" }))
            .Content.ReadFromJsonAsync<ScopeDto>())!;
        Assert.Null(root.ParentId);

        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes",
            new { parentId = root.Id, type = "Laboratory", name = "Haematology" })).StatusCode);

        var list = await owner.GetFromJsonAsync<List<ScopeDto>>($"/api/tenants/{tenant}/scopes");
        Assert.Equal(2, list!.Count);

        // Invalid nesting (a tenant cannot sit under a tenant root) → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes",
            new { parentId = root.Id, type = "Tenant", name = "bad" })).StatusCode);

        // Read-only members may view the tree but not shape it; non-members get 401.
        var (roId, roSession) = await NewUser("scope-ro@example.test");
        await GrantMembership(roId, tenant, "read-only");
        Assert.Equal(HttpStatusCode.OK, (await Session(roSession).GetAsync($"/api/tenants/{tenant}/scopes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Session(roSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/scopes", new { name = "nope" })).StatusCode);
        var (_, strangerSession) = await NewUser("scope-stranger@example.test");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(strangerSession).GetAsync($"/api/tenants/{tenant}/scopes")).StatusCode);
    }

    [Fact]
    public async Task Root_Grant_Elevates_Access_While_A_Child_Scope_Grant_Does_Not_Leak()
    {
        var tenant = await NewTenant("Elevate Lab");
        var (ownerId, ownerSession) = await NewUser("elev-owner@example.test");
        await GrantMembership(ownerId, tenant, "owner");
        var (memberId, memberSession) = await NewUser("elev-member@example.test");
        await GrantMembership(memberId, tenant, "read-only");
        var owner = Session(ownerSession);
        var member = Session(memberSession);

        var root = (await (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes", new { name = "HQ" }))
            .Content.ReadFromJsonAsync<ScopeDto>())!;
        var lab = (await (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes",
            new { parentId = root.Id, type = "Laboratory", name = "Haematology" }))
            .Content.ReadFromJsonAsync<ScopeDto>())!;

        // Baseline: a read-only member cannot issue enrollment tokens (needs ManageFleet).
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);

        // A lab-admin grant at a CHILD scope must not reach a tenant-wide endpoint.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/role-assignments",
            new { userId = memberId, role = "lab-admin", scopeId = lab.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);

        // The same grant at the ROOT scope elevates the member tenant-wide.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/role-assignments",
            new { userId = memberId, role = "lab-admin", scopeId = root.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await member.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);
    }

    [Fact]
    public async Task Lab_Scoped_Grant_Reaches_Only_Its_Own_Gateways()
    {
        var tenant = await NewTenant("Scoped Fleet Lab");
        var (ownerId, ownerSession) = await NewUser("sf-owner@example.test");
        await GrantMembership(ownerId, tenant, "owner");
        var (memberId, memberSession) = await NewUser("sf-member@example.test");
        await GrantMembership(memberId, tenant, "read-only");
        var owner = Session(ownerSession);
        var member = Session(memberSession);

        // Org tree: root → labA, labB. One gateway pinned to each lab.
        var root = (await (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes", new { name = "HQ" }))
            .Content.ReadFromJsonAsync<ScopeDto>())!;
        var labA = await Child(owner, tenant, root.Id, "Laboratory", "Lab A");
        var labB = await Child(owner, tenant, root.Id, "Laboratory", "Lab B");
        var gwA = await EnrollGateway(owner, tenant, "gw-a");
        var gwB = await EnrollGateway(owner, tenant, "gw-b");
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/gateways/{gwA}/scope", new { scopeId = labA.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/gateways/{gwB}/scope", new { scopeId = labB.Id })).StatusCode);

        // Before any grant, the read-only member cannot decommission either gateway.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/api/tenants/{tenant}/gateways/{gwA}/decommission", null)).StatusCode);

        // Grant lab-admin at Lab A only.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/role-assignments",
            new { userId = memberId, role = "lab-admin", scopeId = labA.Id })).StatusCode);

        // Now the member can decommission the Lab A gateway…
        Assert.Equal(HttpStatusCode.NoContent,
            (await member.PostAsync($"/api/tenants/{tenant}/gateways/{gwA}/decommission", null)).StatusCode);
        // …but NOT the Lab B gateway — the grant does not reach a sibling scope.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/api/tenants/{tenant}/gateways/{gwB}/decommission", null)).StatusCode);
    }

    [Fact]
    public async Task Custom_Role_Granted_At_Root_Is_Enforced_By_The_Gate()
    {
        var tenant = await NewTenant("Custom Enforce Lab");
        var (ownerId, ownerSession) = await NewUser("ce-owner@example.test");
        await GrantMembership(ownerId, tenant, "owner");
        var (memberId, memberSession) = await NewUser("ce-member@example.test");
        await GrantMembership(memberId, tenant, "read-only");
        var owner = Session(ownerSession);
        var member = Session(memberSession);

        var root = (await (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/scopes", new { name = "HQ" }))
            .Content.ReadFromJsonAsync<ScopeDto>())!;

        // A read-only member cannot issue enrollment tokens.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);

        // Owner defines a custom role granting exactly the enroll permission, and grants it at root.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/custom-roles",
            new { roleKey = "enroller", name = "Enroller", permissionKeys = FleetEnrollPerm })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/tenants/{tenant}/role-assignments",
            new { userId = memberId, role = "enroller", scopeId = root.Id })).StatusCode);

        // The custom role now takes effect through the gate: the member can enroll.
        Assert.Equal(HttpStatusCode.OK,
            (await member.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);
    }

    [Fact]
    public async Task Delegation_Limits_Surface_As_403_On_Grant_And_Custom_Role()
    {
        var tenant = await NewTenant("Deleg Lab");
        var (ownerId, ownerSession) = await NewUser("deleg-owner@example.test");
        await GrantMembership(ownerId, tenant, "owner");
        var (targetId, _) = await NewUser("deleg-target@example.test");
        var scope = await SeedScope(tenant);
        var owner = Session(ownerSession);

        // Granting the owner role fails delegation (tenant.deactivate is not delegable).
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/role-assignments",
            new { userId = targetId, role = "owner", scopeId = scope })).StatusCode);

        // A custom role with only delegable permissions is created (201) and listed.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/custom-roles",
            new { roleKey = "fleet-viewer", name = "Fleet Viewer", permissionKeys = FleetViewPerm })).StatusCode);
        var roles = await owner.GetFromJsonAsync<List<CustomRoleDto>>($"/api/tenants/{tenant}/custom-roles");
        Assert.Contains(roles!, r => r.RoleKey == "fleet-viewer");

        // A custom role containing a non-delegable permission is refused (403).
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/custom-roles",
            new { roleKey = "danger", name = "Danger", permissionKeys = TenantDeactivatePerm })).StatusCode);
    }
}
