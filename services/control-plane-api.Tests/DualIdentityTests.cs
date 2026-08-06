using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>
/// A request may not be god-mode AND somebody else at the same time.
///
/// IsAdmin matches the Authorization header exactly. CurrentUser only reads that header
/// when it starts with "Bearer ses_", and otherwise falls through to the lc_session
/// cookie. The admin token does not start with "ses_", so a single request carrying the
/// token in the header and any user's session in a cookie was authorized as god-mode
/// while resolving to that user at every `CurrentUser(req, auth)?.User.Id ??
/// "platform-admin"` site — of which there are nineteen.
///
/// The token therefore chose its own actor identity per request. That defeats every
/// control that works by comparing two identities, and two-party approval is exactly
/// such a control: the "distinct second party" it demands could be manufactured by the
/// same person holding one credential.
/// </summary>
public sealed class DualIdentityTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;

    public DualIdentityTests(EmailApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name, bool Active);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record ApprovalDto(string Id, string Status, string RequesterUserId);

    private HttpClient Admin()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return c;
    }

    /// <summary>The token in the header AND a real user's session in the cookie.</summary>
    private HttpClient AdminWearing(string sessionToken)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        c.DefaultRequestHeaders.Add("Cookie", $"lc_session={sessionToken}");
        return c;
    }

    private async Task<(string UserId, string Session)> NewUser()
    {
        var email = $"dual-{Guid.NewGuid():N}@example.test";
        const string password = "correct horse battery staple";
        var created = await Admin().PostAsJsonAsync("/api/platform/users", new { email, password });
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        var login = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, password });
        return (user.Id, (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken);
    }

    [Fact]
    public async Task Admin_Token_Cannot_Borrow_A_Session_Identity()
    {
        var tenant = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "Dual Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;
        var (userId, session) = await NewUser();

        // Request an approval while wearing the user's cookie. The requester must be
        // attributed to the token, not to the borrowed session.
        var created = await AdminWearing(session).PostAsJsonAsync(
            $"/api/tenants/{tenant}/approvals",
            new { permissionKey = Permissions.TenantDeactivate.Key, targetId = tenant, note = "x" });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);

        var approval = (await created.Content.ReadFromJsonAsync<ApprovalDto>())!;
        Assert.Equal("platform-admin", approval.RequesterUserId);
        Assert.NotEqual(userId, approval.RequesterUserId);
    }

    [Fact]
    public async Task Admin_Token_Cannot_Manufacture_Its_Own_Second_Party()
    {
        // The end-to-end property. Permissions.TenantDeactivate is requiresApproval:
        // "a distinct second authorized party must approve". The single-call bypass was
        // closed by routing break-glass through the engine; this is the multi-call one.
        //
        // With one credential: create a user, log in as them, file the request with no
        // cookie (requester = platform-admin), then approve it wearing their cookie
        // (approver = that user). IsDistinctParty compares two different strings and
        // passes, and the tenant is deactivated by one party holding one secret.
        var tenant = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "Second Party Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;
        var (_, session) = await NewUser();

        var created = await Admin().PostAsJsonAsync(
            $"/api/tenants/{tenant}/approvals",
            new { permissionKey = Permissions.TenantDeactivate.Key, targetId = tenant, note = "x" });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var approval = (await created.Content.ReadFromJsonAsync<ApprovalDto>())!;

        // Approving while wearing the borrowed session must NOT count as a second party.
        var approved = await AdminWearing(session).PostAsync(
            $"/api/tenants/{tenant}/approvals/{approval.Id}/approve", null);
        Assert.NotEqual(HttpStatusCode.NoContent, approved.StatusCode);

        // And the governed action must not have happened.
        var tenants = await Admin().GetFromJsonAsync<List<TenantDto>>("/api/tenants");
        Assert.True(tenants!.Single(t => t.Id == tenant).Active,
            "the tenant was deactivated by a single party holding a single credential");
    }
}
