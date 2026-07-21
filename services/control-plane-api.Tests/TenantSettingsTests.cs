using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>Tenant general settings: read (any member) and rename (owner only).</summary>
public sealed class TenantSettingsTests : IClassFixture<AuthApiFactory>
{
    private readonly AuthApiFactory _factory;

    public TenantSettingsTests(AuthApiFactory factory) => _factory = factory;

    private sealed record TenantDto(string Id, string Name, bool Active);
    private sealed record UserDto(string Id);
    private sealed record LoginDto(string SessionToken);

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

    private async Task<string> NewMember(string email, string tenantId, string role)
    {
        var user = (await (await Admin().PostAsJsonAsync("/api/admin/users",
            new { email, password = "correct horse battery staple" })).Content.ReadFromJsonAsync<UserDto>())!;
        await Admin().PostAsJsonAsync("/api/admin/memberships", new { userId = user.Id, tenantId, role });
        var login = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct horse battery staple" });
        return (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken;
    }

    [Fact]
    public async Task Member_Can_Read_Settings_Owner_Can_Rename()
    {
        var tenant = await NewTenant("Original Name");
        var ownerSession = await NewMember("settings-owner@example.test", tenant, "owner");
        var techSession = await NewMember("settings-tech@example.test", tenant, "technician");

        // Any member reads the general settings.
        var read = await Session(techSession).GetFromJsonAsync<TenantDto>($"/api/tenants/{tenant}/settings");
        Assert.Equal("Original Name", read!.Name);

        // Owner renames.
        var renamed = await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/rename", new { name = "  Renamed Lab  " });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Renamed Lab", (await renamed.Content.ReadFromJsonAsync<TenantDto>())!.Name);

        // The change is visible to the other member.
        var reread = await Session(techSession).GetFromJsonAsync<TenantDto>($"/api/tenants/{tenant}/settings");
        Assert.Equal("Renamed Lab", reread!.Name);
    }

    [Fact]
    public async Task Non_Owner_Cannot_Rename()
    {
        var tenant = await NewTenant("Locked Name");
        var techSession = await NewMember("rename-tech@example.test", tenant, "tenant-admin");
        var res = await Session(techSession).PostAsJsonAsync($"/api/tenants/{tenant}/rename", new { name = "Nope" });
        // A member lacking the capability is refused with 403 (non-members get 401).
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Blank_Or_Overlong_Names_Are_Rejected()
    {
        var tenant = await NewTenant("Valid Name");
        var ownerSession = await NewMember("rename-owner@example.test", tenant, "owner");

        Assert.Equal(HttpStatusCode.BadRequest, (await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/rename", new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Session(ownerSession).PostAsJsonAsync(
            $"/api/tenants/{tenant}/rename", new { name = new string('x', 121) })).StatusCode);
    }

    [Fact]
    public async Task Non_Member_Cannot_Read_Settings()
    {
        var tenant = await NewTenant("Private Lab");
        var outsiderSession = await NewMember("outsider@example.test", await NewTenant("Other Lab"), "owner");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(outsiderSession).GetAsync($"/api/tenants/{tenant}/settings")).StatusCode);
    }
}
