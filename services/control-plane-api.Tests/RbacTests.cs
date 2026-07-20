using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Tests;

/// <summary>Tenant isolation, role capabilities, and invitation flows (C3).
/// Reuses EmailApiFactory so invitation emails can be captured.</summary>
public sealed class RbacTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;

    public RbacTests(EmailApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record MembershipDto(string TenantId, string TenantName, string Role);
    private sealed record InvitationDto(string Id, string Email, string Role, string Status);
    private sealed record InviteResponseDto(InvitationDto Invitation, bool EmailDelivered);

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

    private async Task<string> NewTenant(string name)
    {
        var res = await Admin().PostAsJsonAsync("/api/tenants", new { name });
        return (await res.Content.ReadFromJsonAsync<TenantDto>())!.Id;
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

    private async Task Grant(string userId, string tenantId, string role) =>
        Assert.Equal(HttpStatusCode.NoContent, (await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId, tenantId, role })).StatusCode);

    [Fact]
    public async Task Member_Sees_Own_Tenant_But_Not_Another_Tenants_Data()
    {
        var tenantA = await NewTenant("Iso Lab A");
        var tenantB = await NewTenant("Iso Lab B");
        var (userId, session) = await NewUser("iso-owner@example.test");
        await Grant(userId, tenantA, "owner");
        var client = Session(session);

        // Own tenant: gateways + audit readable, enrollment token issuable.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tenants/{tenantA}/gateways")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tenants/{tenantA}/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/tenants/{tenantA}/enrollment-tokens", null)).StatusCode);

        // Cross-tenant (IDOR): everything on tenant B is 401, indistinguishable from no access.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/tenants/{tenantB}/gateways")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/tenants/{tenantB}/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/tenants/{tenantB}/enrollment-tokens", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/tenants/{tenantB}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task Technician_Can_View_But_Not_Manage_Or_Invite()
    {
        var tenant = await NewTenant("Role Lab");
        var (userId, session) = await NewUser("role-tech@example.test");
        await Grant(userId, tenant, "technician");
        var client = Session(session);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/tenants/{tenant}/gateways")).StatusCode);
        // Fleet management, tenant lifecycle, and user management are refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/tenants/{tenant}/enrollment-tokens", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/tenants/{tenant}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/tenants/{tenant}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/tenants/{tenant}/invitations",
            new { email = "x@example.test", role = "technician" })).StatusCode);
    }

    [Fact]
    public async Task Invitation_Email_Round_Trip_Creates_Membership()
    {
        var tenant = await NewTenant("Invite Lab");
        var (adminUserId, adminSession) = await NewUser("invite-admin@example.test");
        await Grant(adminUserId, tenant, "tenant-admin");

        // Admin invites a technician; the email carries a single-use link.
        var invite = await Session(adminSession).PostAsJsonAsync($"/api/tenants/{tenant}/invitations",
            new { email = "invitee@example.test", role = "technician" });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var mail = _factory.Outbox.Sent.Last(e => e.To == "invitee@example.test");
        var token = Regex.Match(mail.TextBody, @"token=([A-Za-z0-9_\-]+)").Groups[1].Value;

        // The invitee signs in with the SAME email and accepts.
        var (_, inviteeSession) = await NewUser("invitee@example.test");
        var accept = await Session(inviteeSession).PostAsJsonAsync("/api/invitations/accept", new { token });
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        // Membership is live: tenant appears in the switcher; gateways readable.
        var memberships = await Session(inviteeSession).GetFromJsonAsync<List<MembershipDto>>("/api/me/memberships");
        Assert.Contains(memberships!, m => m.TenantId == tenant && m.Role == "technician");
        Assert.Equal(HttpStatusCode.OK, (await Session(inviteeSession).GetAsync($"/api/tenants/{tenant}/gateways")).StatusCode);

        // Single-use: replay fails.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Session(inviteeSession).PostAsJsonAsync("/api/invitations/accept", new { token })).StatusCode);
    }

    [Fact]
    public async Task Invitation_Cannot_Be_Accepted_By_A_Different_Email()
    {
        var tenant = await NewTenant("Mismatch Lab");
        var (adminUserId, adminSession) = await NewUser("mismatch-admin@example.test");
        await Grant(adminUserId, tenant, "owner");

        await Session(adminSession).PostAsJsonAsync($"/api/tenants/{tenant}/invitations",
            new { email = "intended@example.test", role = "auditor" });
        var token = Regex.Match(_factory.Outbox.Sent.Last(e => e.To == "intended@example.test").TextBody,
            @"token=([A-Za-z0-9_\-]+)").Groups[1].Value;

        var (_, thiefSession) = await NewUser("someone-else@example.test");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Session(thiefSession).PostAsJsonAsync("/api/invitations/accept", new { token })).StatusCode);
    }

    [Fact]
    public async Task Revoked_Invitation_Cannot_Be_Accepted()
    {
        var tenant = await NewTenant("Revoke Lab");
        var (adminUserId, adminSession) = await NewUser("revoke-admin@example.test");
        await Grant(adminUserId, tenant, "owner");
        var admin = Session(adminSession);

        var created = await admin.PostAsJsonAsync($"/api/tenants/{tenant}/invitations",
            new { email = "revoked@example.test", role = "read-only" });
        var invitation = (await created.Content.ReadFromJsonAsync<InviteResponseDto>())!.Invitation;
        var token = Regex.Match(_factory.Outbox.Sent.Last(e => e.To == "revoked@example.test").TextBody,
            @"token=([A-Za-z0-9_\-]+)").Groups[1].Value;

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsync($"/api/tenants/{tenant}/invitations/{invitation.Id}/revoke", null)).StatusCode);

        var (_, session) = await NewUser("revoked@example.test");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Session(session).PostAsJsonAsync("/api/invitations/accept", new { token })).StatusCode);

        var list = await admin.GetFromJsonAsync<List<InvitationDto>>($"/api/tenants/{tenant}/invitations");
        Assert.Equal("revoked", list!.Single(i => i.Id == invitation.Id).Status);
    }

    [Fact]
    public async Task Unknown_Role_Is_Rejected_Everywhere()
    {
        var tenant = await NewTenant("Bad Role Lab");
        var (userId, session) = await NewUser("badrole@example.test");
        await Grant(userId, tenant, "owner");

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin().PostAsJsonAsync("/api/admin/memberships",
            new { userId, tenantId = tenant, role = "super-root" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Session(session).PostAsJsonAsync(
            $"/api/tenants/{tenant}/invitations", new { email = "x@example.test", role = "super-root" })).StatusCode);
    }
}
