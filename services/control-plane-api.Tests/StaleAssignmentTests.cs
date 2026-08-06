using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Demotion must actually demote.
///
/// Forbidden authorizes against the UNION of the caller's membership role and their
/// persisted root-scope role assignments (Program.cs RootScopeContext). The startup
/// backfill (MembershipAssignmentBackfill) gave every existing member a permanent
/// root assignment mirroring their role — ExpiresAt null, RevokedAt null — and neither
/// ChangeRole nor RemoveMember ever revoked it.
///
/// Because the union is permissive, the stale assignment wins: demoting an owner to
/// read-only changed the membership row and nothing else, so they kept owner authority.
/// This needs no admin token and survives its retirement entirely.
/// </summary>
public sealed class StaleAssignmentTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;

    public StaleAssignmentTests(EmailApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record AssignmentDto(string Id, string UserId, string Role, bool Active);
    private sealed record ScopeDto(string Id, string Type, string Name, string? ParentId, string Path);

    /// <summary>
    /// Materialise the tenant root scope. In a real deployment
    /// MembershipAssignmentBackfill creates this at startup; a tenant created in-test
    /// has no persisted scopes, and RootScopeContext short-circuits to a synthetic tree
    /// with membership only when there are none — which is precisely the case where the
    /// bug cannot appear. The root must exist for this to reproduce.
    /// </summary>
    private static async Task<string> RootScopeId(HttpClient client, string tenant)
    {
        await client.PostAsJsonAsync($"/api/tenants/{tenant}/scopes",
            new { parentId = (string?)null, type = (string?)null, name = tenant });
        var scopes = await client.GetFromJsonAsync<List<ScopeDto>>($"/api/tenants/{tenant}/scopes");
        return scopes!.Single(s => s.ParentId is null).Id;
    }

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

    private async Task<(string UserId, string Session)> NewUser()
    {
        var email = $"stale-{Guid.NewGuid():N}@example.test";
        const string password = "correct horse battery staple";
        var created = await Admin().PostAsJsonAsync("/api/platform/users", new { email, password });
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        var login = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, password });
        return (user.Id, (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken);
    }

    [Fact]
    public async Task Demotion_Revokes_The_Root_Assignment_That_Mirrors_The_Old_Role()
    {
        var tenant = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "Stale Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;

        var (ownerId, ownerSession) = await NewUser();
        var (victimId, victimSession) = await NewUser();
        await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId = ownerId, tenantId = tenant, role = Roles.Owner });
        await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId = victimId, tenantId = tenant, role = Roles.TenantAdmin });

        // Stand in for the startup backfill: a root-scope assignment mirroring the
        // member's role, which is exactly the row MembershipAssignmentBackfill creates
        // (ExpiresAt null, RevokedAt null).
        var rootScope = await RootScopeId(Session(ownerSession), tenant);
        var granted = await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/role-assignments",
            new { userId = victimId, role = Roles.TenantAdmin, scopeId = rootScope });
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);

        // Demote them to the least-privileged role.
        var demote = await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{victimId}/role", new { role = Roles.ReadOnly });
        Assert.Equal(HttpStatusCode.NoContent, demote.StatusCode);

        // The demotion must bite. Inviting a member needs ManageUsers, which tenant-admin
        // has and read-only does not. Before the fix this succeeded — the stale root
        // assignment was unioned back in and outranked the demoted membership row.
        var invite = await Session(victimSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/invitations",
            new { email = $"ghost-{Guid.NewGuid():N}@example.test", role = Roles.ReadOnly });
        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);

        // And the assignment itself is gone, not merely out-ranked.
        var assignments = await Session(ownerSession)
            .GetFromJsonAsync<List<AssignmentDto>>($"/api/tenants/{tenant}/role-assignments");
        Assert.DoesNotContain(assignments!,
            a => a.UserId == victimId && a.Role == Roles.TenantAdmin && a.Active);
    }

    [Fact]
    public async Task Deliberate_Sub_Scope_Grants_Survive_A_Tenant_Wide_Demotion()
    {
        // The control on the fix: revoking must be surgical. A scoped grant an owner
        // made deliberately ("admin of this one site") expresses a different intent from
        // the root row that merely mirrors membership, and a demotion elsewhere must not
        // silently destroy it. Without this, the fix would be an over-correction that
        // quietly removes access nobody asked to remove.
        var tenant = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "Scoped Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;

        var (ownerId, ownerSession) = await NewUser();
        var (victimId, _) = await NewUser();
        await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId = ownerId, tenantId = tenant, role = Roles.Owner });
        await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId = victimId, tenantId = tenant, role = Roles.TenantAdmin });

        // A genuine CHILD scope. Posting parentId=null is the idempotent tenant-root
        // create, not a sub-scope — grant there and it is tenant-wide, so the fix
        // revokes it correctly and this control would prove nothing.
        var rootScope = await RootScopeId(Session(ownerSession), tenant);
        var created = await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/scopes", new { name = "Site A", type = "site", parentId = rootScope });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var siteId = (await created.Content.ReadFromJsonAsync<ScopeDto>())!.Id;
        Assert.NotEqual(rootScope, siteId);

        var grant = await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/role-assignments",
            new { userId = victimId, role = Roles.TenantAdmin, scopeId = siteId });
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);

        var demote = await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{victimId}/role", new { role = Roles.ReadOnly });
        Assert.Equal(HttpStatusCode.NoContent, demote.StatusCode);

        var assignments = await Session(ownerSession)
            .GetFromJsonAsync<List<AssignmentDto>>($"/api/tenants/{tenant}/role-assignments");
        Assert.Contains(assignments!, a => a.UserId == victimId && a.Active);
    }
}
