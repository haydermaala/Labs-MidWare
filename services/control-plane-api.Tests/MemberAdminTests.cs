using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>Role changes and member removal (D3a): privilege-escalation and
/// tenant-lockout guards.</summary>
public sealed class MemberAdminTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;

    public MemberAdminTests(EmailApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken);
    private sealed record MemberDto(string UserId, string Email, string Role, bool Active);

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
        (await (await Admin().PostAsJsonAsync("/api/tenants", new { name })).Content
            .ReadFromJsonAsync<TenantDto>())!.Id;

    private async Task<(string UserId, string Session)> NewUser(string email)
    {
        var created = await Admin().PostAsJsonAsync("/api/admin/users",
            new { email, password = "correct horse battery staple" });
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        return (user.Id, (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken);
    }

    private async Task Grant(string userId, string tenantId, string role) =>
        await Admin().PostAsJsonAsync("/api/admin/memberships", new { userId, tenantId, role });

    [Fact]
    public async Task Owner_Can_Change_A_Members_Role()
    {
        var tenant = await NewTenant("Role Change Lab");
        var (ownerId, ownerSession) = await NewUser("rc-owner@example.test");
        var (techId, _) = await NewUser("rc-tech@example.test");
        await Grant(ownerId, tenant, "owner");
        await Grant(techId, tenant, "technician");

        var res = await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{techId}/role", new { role = "lab-admin" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var members = await Session(ownerSession).GetFromJsonAsync<List<MemberDto>>($"/api/tenants/{tenant}/members");
        Assert.Equal("lab-admin", members!.Single(m => m.UserId == techId).Role);
    }

    [Fact]
    public async Task The_Last_Owner_Cannot_Be_Demoted_Or_Removed()
    {
        var tenant = await NewTenant("Lockout Lab");
        var (ownerId, ownerSession) = await NewUser("lock-owner@example.test");
        await Grant(ownerId, tenant, "owner");
        var owner = Session(ownerSession);

        var demote = await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{ownerId}/role", new { role = "technician" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);

        var remove = await owner.PostAsync($"/api/tenants/{tenant}/members/{ownerId}/remove", null);
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);

        // Still an owner, so the tenant is never stranded.
        var members = await owner.GetFromJsonAsync<List<MemberDto>>($"/api/tenants/{tenant}/members");
        Assert.Equal("owner", members!.Single(m => m.UserId == ownerId).Role);
    }

    [Fact]
    public async Task A_Second_Owner_Makes_Demotion_Possible()
    {
        var tenant = await NewTenant("Two Owner Lab");
        var (firstId, firstSession) = await NewUser("two-owner-a@example.test");
        var (secondId, _) = await NewUser("two-owner-b@example.test");
        await Grant(firstId, tenant, "owner");
        await Grant(secondId, tenant, "owner");

        var res = await Session(firstSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{firstId}/role", new { role = "auditor" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task Tenant_Admin_Cannot_Grant_Or_Revoke_Owner()
    {
        var tenant = await NewTenant("Escalation Lab");
        var (ownerId, _) = await NewUser("esc-owner@example.test");
        var (adminId, adminSession) = await NewUser("esc-admin@example.test");
        var (techId, _) = await NewUser("esc-tech@example.test");
        await Grant(ownerId, tenant, "owner");
        await Grant(adminId, tenant, "tenant-admin");
        await Grant(techId, tenant, "technician");
        var admin = Session(adminSession);

        // Cannot promote anyone (including themselves) to owner.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{techId}/role", new { role = "owner" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{adminId}/role", new { role = "owner" })).StatusCode);

        // Cannot demote or remove an existing owner either.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{ownerId}/role", new { role = "technician" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PostAsync($"/api/tenants/{tenant}/members/{ownerId}/remove", null)).StatusCode);

        // Cannot invite an owner (the same privilege grant by another route).
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync(
            $"/api/tenants/{tenant}/invitations", new { email = "new-owner@example.test", role = "owner" })).StatusCode);

        // But ordinary role management still works.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{techId}/role", new { role = "auditor" })).StatusCode);
    }

    [Fact]
    public async Task Removed_Member_Loses_Tenant_Access()
    {
        var tenant = await NewTenant("Removal Lab");
        var (ownerId, ownerSession) = await NewUser("rm-owner@example.test");
        var (techId, techSession) = await NewUser("rm-tech@example.test");
        await Grant(ownerId, tenant, "owner");
        await Grant(techId, tenant, "technician");

        Assert.Equal(HttpStatusCode.OK, (await Session(techSession).GetAsync($"/api/tenants/{tenant}/gateways")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await Session(ownerSession).PostAsync($"/api/tenants/{tenant}/members/{techId}/remove", null)).StatusCode);

        // Access is gone and the tenant no longer appears in their switcher.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(techSession).GetAsync($"/api/tenants/{tenant}/gateways")).StatusCode);
        var memberships = await Session(techSession).GetFromJsonAsync<List<Dictionary<string, object>>>("/api/me/memberships");
        Assert.DoesNotContain(memberships!, m => m["tenantId"].ToString() == tenant);
    }

    [Fact]
    public async Task Technician_Cannot_Manage_Members_At_All()
    {
        var tenant = await NewTenant("No Manage Lab");
        var (ownerId, _) = await NewUser("nm-owner@example.test");
        var (techId, techSession) = await NewUser("nm-tech@example.test");
        await Grant(ownerId, tenant, "owner");
        await Grant(techId, tenant, "technician");
        var tech = Session(techSession);

        // A member who lacks the capability is refused with 403 (non-members get 401).
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{ownerId}/role", new { role = "read-only" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await tech.PostAsync($"/api/tenants/{tenant}/members/{ownerId}/remove", null)).StatusCode);
    }

    [Fact]
    public async Task Unknown_Role_And_Unknown_Member_Are_Rejected()
    {
        var tenant = await NewTenant("Validation Lab");
        var (ownerId, ownerSession) = await NewUser("val-owner@example.test");
        await Grant(ownerId, tenant, "owner");
        var owner = Session(ownerSession);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/{ownerId}/role", new { role = "super-root" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(
            $"/api/tenants/{tenant}/members/usr_missing/role", new { role = "auditor" })).StatusCode);
    }
}
