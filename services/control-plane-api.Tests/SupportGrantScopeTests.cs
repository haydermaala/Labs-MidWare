using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>
/// A support grant confers read-only, and nothing may quietly widen it.
///
/// Forbidden sets `role = Roles.ReadOnly` for a support-grant caller and promises
/// "read-only and nothing more: diagnosis, never mutation". But the very next step,
/// RootScopeContext, unions the caller's persisted root-scope assignments into the
/// decision — and the union is permissive.
///
/// So a former member who still carried a stale root assignment would authorize as that
/// old role, not read-only, including mutations. The audit flagged this composition but
/// marked it INFER: both halves were verified independently, the composed path was never
/// executed. It is executed here.
///
/// It holds because RemoveMember now revokes root assignments. This test is what stops a
/// future change to either half from silently re-opening it — neither half looks like it
/// owns the read-only promise, which is exactly why it needs a test that spans both.
/// </summary>
public sealed class SupportGrantScopeTests : IClassFixture<IsolatedApiFactory>
{
    private readonly IsolatedApiFactory _factory;

    public SupportGrantScopeTests(IsolatedApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record ScopeDto(string Id, string Type, string Name, string? ParentId, string Path);
    private sealed record GrantDto(string Id, string SubjectTenantId, string RequesterUserId,
        string Reason, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
        string? DecidedByUserId, bool Active);

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
        var email = $"sg-{Guid.NewGuid():N}@example.test";
        const string password = "correct horse battery staple";
        var created = await Admin().PostAsJsonAsync("/api/platform/users", new { email, password });
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        var login = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, password });
        return (user.Id, (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken);
    }

    private static async Task<string> RootScopeId(HttpClient client, string tenant)
    {
        await client.PostAsJsonAsync($"/api/tenants/{tenant}/scopes",
            new { parentId = (string?)null, type = (string?)null, name = tenant });
        var scopes = await client.GetFromJsonAsync<List<ScopeDto>>($"/api/tenants/{tenant}/scopes");
        return scopes!.Single(s => s.ParentId is null).Id;
    }

    [Fact]
    public async Task A_Removed_Member_With_A_Support_Grant_Gets_Read_Only_Not_Their_Old_Role()
    {
        var tenant = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "Grant Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;

        var (ownerId, ownerSession) = await NewUser();
        var (exMemberId, exMemberSession) = await NewUser();
        await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId = ownerId, tenantId = tenant, role = Roles.Owner });
        await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId = exMemberId, tenantId = tenant, role = Roles.TenantAdmin });

        // The stale root assignment the startup backfill would have created.
        var rootScope = await RootScopeId(Session(ownerSession), tenant);
        Assert.Equal(HttpStatusCode.Created, (await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/role-assignments",
            new { userId = exMemberId, role = Roles.TenantAdmin, scopeId = rootScope })).StatusCode);

        // They leave the tenant.
        Assert.Equal(HttpStatusCode.NoContent, (await Session(ownerSession)
            .PostAsync($"/api/tenants/{tenant}/members/{exMemberId}/remove", null)).StatusCode);

        // No membership, no grant yet: invisible.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(exMemberSession).GetAsync($"/api/tenants/{tenant}/gateways")).StatusCode);

        // Now hand them an approved support grant. Requested and approved by two distinct
        // platform users, so the separation-of-duty check is genuinely satisfied.
        await Admin().PostAsJsonAsync("/api/platform/role-assignments",
            new { userId = exMemberId, role = PlatformRoles.SupportEngineer });
        var requested = await Session(exMemberSession).PostAsJsonAsync("/api/platform/support-grants",
            new { subjectTenantId = tenant, reason = "incident", durationMinutes = 60 });
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var grant = (await requested.Content.ReadFromJsonAsync<GrantDto>())!;

        var (approverId, approverSession) = await NewUser();
        await Admin().PostAsJsonAsync("/api/platform/role-assignments",
            new { userId = approverId, role = PlatformRoles.SecurityAdmin });
        Assert.Equal(HttpStatusCode.NoContent, (await Session(approverSession)
            .PostAsync($"/api/platform/support-grants/{grant.Id}/approve", null)).StatusCode);

        // The grant is live: reads work. That is the feature.
        Assert.Equal(HttpStatusCode.OK,
            (await Session(exMemberSession).GetAsync($"/api/tenants/{tenant}/gateways")).StatusCode);

        // And it is READ-ONLY. Renaming needs owner; inviting needs ManageUsers. Their old
        // tenant-admin role had ManageUsers, so if the stale assignment were still unioned
        // in, the invite would succeed and the read-only promise would be false.
        Assert.Equal(HttpStatusCode.Forbidden, (await Session(exMemberSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/invitations",
            new { email = $"x-{Guid.NewGuid():N}@example.test", role = Roles.ReadOnly })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await Session(exMemberSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/rename", new { name = "Owned Via A Ticket" })).StatusCode);
    }
}
