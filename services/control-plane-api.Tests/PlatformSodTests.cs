using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>
/// One human must not hold both sides of the support-grant approval.
///
/// The platform had only dynamic separation of duty: PlatformSupportService refuses to let
/// a requester approve their own grant. Nothing stopped the same person being granted both
/// platform-support-engineer (request) and platform-security-admin (approve), which makes
/// the dynamic check a formality — request under one hat, approve under the other, two
/// sessions, and every audit row looks right because two genuinely distinct role
/// assignments were involved.
///
/// The grant path is the only place a platform role is conferred, so it is the only place
/// this has to hold.
/// </summary>
public sealed class PlatformSodTests : IClassFixture<IsolatedApiFactory>
{
    private readonly IsolatedApiFactory _factory;

    public PlatformSodTests(IsolatedApiFactory factory) => _factory = factory;

    private sealed record UserDto(string Id, string Email);

    private HttpClient Admin()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        return c;
    }

    private async Task<string> NewUser()
    {
        var email = $"sod-{Guid.NewGuid():N}@example.test";
        var created = await Admin().PostAsJsonAsync("/api/platform/users",
            new { email, password = "correct horse battery staple" });
        return (await created.Content.ReadFromJsonAsync<UserDto>())!.Id;
    }

    private async Task<HttpResponseMessage> Grant(string userId, string role) =>
        await Admin().PostAsJsonAsync("/api/platform/role-assignments", new { userId, role });

    [Theory]
    [InlineData(PlatformRoles.SupportEngineer, PlatformRoles.SecurityAdmin)]
    [InlineData(PlatformRoles.SecurityAdmin, PlatformRoles.SupportEngineer)]
    public async Task Cannot_Hold_Both_Sides_Of_Support_Approval(string first, string second)
    {
        // Order must not matter: the rule is about the resulting combination, not the
        // sequence it was assembled in.
        var user = await NewUser();

        Assert.Equal(HttpStatusCode.Created, (await Grant(user, first)).StatusCode);

        var conflict = await Grant(user, second);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var body = await conflict.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Contains("rules", body!.Keys);
    }

    [Fact]
    public async Task Either_Side_Alone_Is_Fine()
    {
        Assert.Equal(HttpStatusCode.Created,
            (await Grant(await NewUser(), PlatformRoles.SupportEngineer)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await Grant(await NewUser(), PlatformRoles.SecurityAdmin)).StatusCode);
    }

    [Fact]
    public async Task Unrelated_Roles_Are_Unaffected()
    {
        // The rule must be narrow. Guarding against an over-broad implementation that
        // refuses any second role.
        var user = await NewUser();
        Assert.Equal(HttpStatusCode.Created, (await Grant(user, PlatformRoles.SupportEngineer)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Grant(user, PlatformRoles.BillingAdmin)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Grant(user, PlatformRoles.ReleaseManager)).StatusCode);
    }

    [Fact]
    public async Task Root_Owner_Is_Exempt_And_Does_Not_Freeze_Later_Grants()
    {
        // Root Owner holds every permission, so by construction it holds both sides of
        // every pair. That is what break-glass means — it is Critical and MFA-gated, and
        // dynamic SoD still stops a Root Owner approving their own request.
        var user = await NewUser();
        Assert.Equal(HttpStatusCode.Created, (await Grant(user, PlatformRoles.RootOwner)).StatusCode);

        // And a pre-existing violation must not block unrelated grants, or introducing a
        // rule would freeze the affected accounts out of every future role change.
        Assert.Equal(HttpStatusCode.Created, (await Grant(user, PlatformRoles.BillingAdmin)).StatusCode);
    }

    [Fact]
    public void The_Rule_Set_Is_Grounded_In_Real_Permission_Keys()
    {
        // A rule naming a permission that does not exist can never fire, and would look
        // like enforcement while enforcing nothing.
        var known = PlatformPermissions.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(PlatformSeparationOfDuty.Rules);
        Assert.All(PlatformSeparationOfDuty.Rules, r =>
        {
            Assert.Contains(r.PermissionA, known);
            Assert.Contains(r.PermissionB, known);
            Assert.NotEqual(r.PermissionA, r.PermissionB);
        });
    }
}
