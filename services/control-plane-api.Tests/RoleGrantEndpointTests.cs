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

    public RoleGrantEndpointTests(EmailApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record AssignmentDto(string Id, string UserId, string Role, string ScopeId, bool Active);
    private sealed record CustomRoleDto(string RoleKey, string Name, List<string> PermissionKeys);
    private sealed record ScopeDto(string Id, string Type, string Name, string? ParentId, string Path);

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
