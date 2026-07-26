using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>P6 platform super-admin HTTP surface: gated by PLATFORM roles (not tenant
/// membership), Root-Owner-only role management, god-mode-token bootstrap, and the
/// disjointness invariant — a tenant role grants no platform access.</summary>
public sealed class PlatformEndpointTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;

    public PlatformEndpointTests(EmailApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record AssignmentDto(string Id, string UserId, string Role, bool Active);
    private sealed record SupportGrantDto(string Id, string SubjectTenantId, string Status, bool Active);
    private sealed record SecurityEventDto(string Id, DateTimeOffset At, string Kind, string ActorUserId, string Detail);

    private async Task<string> NewTenant(string name) =>
        (await (await Admin().PostAsJsonAsync("/api/tenants", new { name }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;

    private async Task GrantPlatformRole(string userId, string role) =>
        await Admin().PostAsJsonAsync("/api/platform/role-assignments", new { userId, role });

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

    private async Task<(string UserId, string Session)> NewUser(string email)
    {
        var created = await Admin().PostAsJsonAsync("/api/admin/users",
            new { email, password = "correct horse battery staple" });
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        return (user.Id, (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken);
    }

    [Fact]
    public async Task Bootstrap_Then_Platform_Roles_Enforce()
    {
        var (userId, session) = await NewUser("plat-user@example.test");
        var user = Session(session);

        // Not a platform user → 401 on any platform endpoint (anti-enumeration).
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/platform/tenants")).StatusCode);

        // God-mode token bootstraps: grant the user the Auditor platform role.
        var grant = await Admin().PostAsJsonAsync("/api/platform/role-assignments",
            new { userId, role = PlatformRoles.Auditor });
        Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var assignment = (await grant.Content.ReadFromJsonAsync<AssignmentDto>())!;
        Assert.True(assignment.Active);

        // Now the user can read the platform tenant registry (Auditor holds platform.tenant.read)…
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/platform/tenants")).StatusCode);

        // …but cannot manage platform roles (Root-Owner-only) → 403.
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync(
            "/api/platform/role-assignments", new { userId, role = PlatformRoles.ReleaseManager })).StatusCode);

        // Revoke via the god-mode token; the user loses platform access again.
        Assert.Equal(HttpStatusCode.NoContent,
            (await Admin().DeleteAsync($"/api/platform/role-assignments/{assignment.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task A_Tenant_Role_Grants_No_Platform_Access()
    {
        var tenantId = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "Plat Iso" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;
        var (ownerId, ownerSession) = await NewUser("plat-owner@example.test");
        await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId = ownerId, tenantId, role = "owner" });

        // A tenant OWNER is not a platform user — platform access is disjoint.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(ownerSession).GetAsync("/api/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task Ops_Manages_Tenant_Lifecycle_And_Billing_Manages_Subscriptions()
    {
        // Operations Admin: provision + suspend/reactivate, but NOT subscriptions.
        var (opsId, opsSession) = await NewUser("plat-ops@example.test");
        await Admin().PostAsJsonAsync("/api/platform/role-assignments",
            new { userId = opsId, role = PlatformRoles.OperationsAdmin });
        var ops = Session(opsSession);

        var tenant = (await (await ops.PostAsJsonAsync("/api/platform/tenants", new { name = "Provisioned Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!;
        Assert.False(string.IsNullOrEmpty(tenant.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await ops.PostAsync($"/api/platform/tenants/{tenant.Id}/suspend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ops.PostAsync($"/api/platform/tenants/{tenant.Id}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ops.PostAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/subscription", new { planId = "laboratory" })).StatusCode);

        // Billing Admin: set the plan, but NOT provision.
        var (billId, billSession) = await NewUser("plat-bill@example.test");
        await Admin().PostAsJsonAsync("/api/platform/role-assignments",
            new { userId = billId, role = PlatformRoles.BillingAdmin });
        var bill = Session(billSession);

        Assert.Equal(HttpStatusCode.OK, (await bill.PostAsJsonAsync(
            $"/api/platform/tenants/{tenant.Id}/subscription", new { planId = "laboratory" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bill.PostAsJsonAsync(
            "/api/platform/tenants", new { name = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task Support_Access_Needs_A_Distinct_Approver()
    {
        var tenant = await NewTenant("Support Subject Co");
        var (reqId, reqSession) = await NewUser("plat-sup-req@example.test");
        await GrantPlatformRole(reqId, PlatformRoles.SupportEngineer);
        var requester = Session(reqSession);
        var (secId, secSession) = await NewUser("plat-security@example.test");
        await GrantPlatformRole(secId, PlatformRoles.SecurityAdmin);
        var security = Session(secSession);

        // Support engineer requests time-limited access.
        var opened = await requester.PostAsJsonAsync("/api/platform/support-grants",
            new { subjectTenantId = tenant, reason = "incident 42", durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.Accepted, opened.StatusCode);
        var grant = (await opened.Content.ReadFromJsonAsync<SupportGrantDto>())!;

        // A support engineer cannot APPROVE (lacks platform.support.approve).
        Assert.Equal(HttpStatusCode.Forbidden,
            (await requester.PostAsync($"/api/platform/support-grants/{grant.Id}/approve", null)).StatusCode);

        // A distinct Security admin sees it pending and approves it.
        var pending = await security.GetFromJsonAsync<List<SupportGrantDto>>("/api/platform/support-grants");
        Assert.Contains(pending!, g => g.Id == grant.Id);
        Assert.Equal(HttpStatusCode.NoContent,
            (await security.PostAsync($"/api/platform/support-grants/{grant.Id}/approve", null)).StatusCode);
        // Re-deciding a decided grant → 409.
        Assert.Equal(HttpStatusCode.Conflict,
            (await security.PostAsync($"/api/platform/support-grants/{grant.Id}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Same_Party_Cannot_Self_Approve_Support_Access()
    {
        var tenant = await NewTenant("Self Approve Co");
        // A user holding BOTH support + security roles still cannot approve their own request.
        var (uid, session) = await NewUser("plat-both@example.test");
        await GrantPlatformRole(uid, PlatformRoles.SupportEngineer);
        await GrantPlatformRole(uid, PlatformRoles.SecurityAdmin);
        var user = Session(session);

        var grant = (await (await user.PostAsJsonAsync("/api/platform/support-grants",
            new { subjectTenantId = tenant, reason = "self", durationMinutes = 15 }))
            .Content.ReadFromJsonAsync<SupportGrantDto>())!;

        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.PostAsync($"/api/platform/support-grants/{grant.Id}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Privileged_Platform_Operations_Emit_Security_Events()
    {
        // A granted platform role is itself a security event; a Security admin (which
        // holds platform.security_event.read) can review the log.
        var (secId, secSession) = await NewUser("plat-sec-audit@example.test");
        await GrantPlatformRole(secId, PlatformRoles.SecurityAdmin);
        var security = Session(secSession);

        var events = await security.GetFromJsonAsync<List<SecurityEventDto>>("/api/platform/security-events");
        // The grant that provisioned this very Security admin was recorded.
        Assert.Contains(events!, e => e.Kind == "platform.role.granted" && e.Detail.Contains(secId));

        // An Auditor also holds security_event.read; a role with neither is denied.
        var (opsId, opsSession) = await NewUser("plat-ops-audit@example.test");
        await GrantPlatformRole(opsId, PlatformRoles.ReleaseManager); // no security_event.read
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Session(opsSession).GetAsync("/api/platform/security-events")).StatusCode);
    }

    [Fact]
    public async Task Unknown_Platform_Role_Is_Rejected()
    {
        var (userId, _) = await NewUser("plat-bad@example.test");
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsJsonAsync(
            "/api/platform/role-assignments", new { userId, role = "platform-god" })).StatusCode);
        // A tenant role is not a platform role either.
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsJsonAsync(
            "/api/platform/role-assignments", new { userId, role = Roles.Owner })).StatusCode);
    }
}
