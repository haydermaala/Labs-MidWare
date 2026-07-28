using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlPlane.Api.Tests;

/// <summary>Cross-cutting security regression suite. The load-bearing test walks the
/// live route table and asserts that EVERY protected admin endpoint
/// (/api/tenants/* and /api/platform/*) refuses an unauthenticated caller — so a new
/// endpoint can never ship accidentally ungated. Plus the disjointness invariants
/// between tenant and platform authorization.</summary>
public sealed class SecurityRegressionTests : IClassFixture<EmailApiFactory>
{
    private readonly EmailApiFactory _factory;
    private static readonly string[] DefaultMethods = ["GET"];

    public SecurityRegressionTests(EmailApiFactory factory) => _factory = factory;

    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);
    private sealed record TenantDto(string Id, string Name);

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

    // Protected admin surfaces: session/admin/platform gated. (Device-plane
    // /api/gateways/* authenticate by device credential, and /api/auth/* + /health are
    // deliberately public, so they are excluded from this session-auth invariant.)
    private static bool IsProtectedAdminRoute(string pattern) =>
        pattern.StartsWith("/api/tenants/", StringComparison.Ordinal) ||
        pattern.StartsWith("/api/platform/", StringComparison.Ordinal);

    [Fact]
    public async Task No_Protected_Endpoint_Is_Reachable_Unauthenticated()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (
                Pattern: e.RoutePattern.RawText ?? "",
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? DefaultMethods))
            .Where(e => IsProtectedAdminRoute(e.Pattern))
            .ToList();

        Assert.True(endpoints.Count >= 20, $"expected a sizeable protected surface, found {endpoints.Count}");

        var anon = _factory.CreateClient();
        var leaks = new List<string>();
        foreach (var (pattern, methods) in endpoints)
        {
            // Substitute route parameters with a probe value.
            var path = Regex.Replace(pattern, "\\{[^}]+\\}", "probe");
            foreach (var method in methods)
            {
                var req = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT" or "PATCH")
                {
                    req.Content = JsonContent.Create(new { });
                }
                var res = await anon.SendAsync(req);
                // The invariant: an unauthenticated caller never gets a success (2xx)
                // from a protected endpoint. 401/403/400/404 are all fine.
                if ((int)res.StatusCode is >= 200 and < 300)
                {
                    leaks.Add($"{method} {path} -> {(int)res.StatusCode}");
                }
            }
        }

        Assert.True(leaks.Count == 0, "unauthenticated 2xx from protected endpoints:\n  " + string.Join("\n  ", leaks));
    }

    [Fact]
    public async Task Platform_And_Tenant_Authorization_Are_Disjoint()
    {
        var tenant = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "Disjoint Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;

        // A tenant OWNER has no platform access.
        var (ownerId, ownerSession) = await NewUser("sec-owner@example.test");
        await Admin().PostAsJsonAsync("/api/admin/memberships", new { userId = ownerId, tenantId = tenant, role = "owner" });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(ownerSession).GetAsync("/api/platform/tenants")).StatusCode);

        // A platform AUDITOR has no tenant access (not a member of the tenant).
        var (audId, audSession) = await NewUser("sec-auditor@example.test");
        await Admin().PostAsJsonAsync("/api/platform/role-assignments", new { userId = audId, role = PlatformRoles.Auditor });
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Session(audSession).GetAsync($"/api/tenants/{tenant}/gateways")).StatusCode);
    }

    [Fact]
    public async Task A_ReadOnly_Platform_Role_Is_Refused_Every_Mutating_Platform_Action()
    {
        // Least-privilege regression across the P6/P7 platform surface: a platform
        // AUDITOR (read-only) is a platform user, so it is NOT anti-enum 401'd — but it
        // must be 403'd on every action it lacks the specific permission for. Authorization
        // runs before any tenant lookup, so a probe id is fine (the denial precedes it).
        var (audId, audSession) = await NewUser("sec-lp-auditor@example.test");
        await Admin().PostAsJsonAsync("/api/platform/role-assignments",
            new { userId = audId, role = PlatformRoles.Auditor });
        var auditor = Session(audSession);

        // Reads the Auditor IS entitled to → 200 (proves it really is a platform user).
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/platform/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync("/api/platform/security-events")).StatusCode);

        // Every mutating platform action → 403 (has a platform role, lacks THIS permission).
        var forbidden = new (string Method, string Path)[]
        {
            ("POST", "/api/platform/tenants"),                              // TenantProvision
            ("POST", "/api/platform/tenants/probe/suspend"),               // TenantSuspend
            ("POST", "/api/platform/tenants/probe/reactivate"),           // TenantSuspend
            ("POST", "/api/platform/tenants/probe/archive"),              // TenantOffboard
            ("POST", "/api/platform/tenants/probe/cancel-offboarding"),   // TenantOffboard
            ("POST", "/api/platform/tenants/probe/legal-hold"),           // TenantOffboard
            ("GET",  "/api/platform/tenants/probe/export"),               // TenantExport
            ("POST", "/api/platform/tenants/probe/subscription"),         // SubscriptionManage
            ("POST", "/api/platform/offboard-requests"),                   // TenantOffboard
            ("POST", "/api/platform/support-grants"),                      // SupportRequest
            ("POST", "/api/platform/role-assignments"),                    // RoleManage
        };

        var leaks = new List<string>();
        foreach (var (method, path) in forbidden)
        {
            var req = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST")
            {
                req.Content = JsonContent.Create(new { });
            }
            var res = await auditor.SendAsync(req);
            if (res.StatusCode != HttpStatusCode.Forbidden)
            {
                leaks.Add($"{method} {path} -> {(int)res.StatusCode} (expected 403)");
            }
        }

        Assert.True(leaks.Count == 0,
            "a read-only auditor was NOT refused a mutating platform action:\n  " + string.Join("\n  ", leaks));
    }

    [Fact]
    public async Task All_Platform_Tables_Are_Global_Not_Tenant_Scoped()
    {
        // A structural guard complementing RlsCoverageTests: no platform_* table may
        // carry a TenantId (they are global, keyed on user / SubjectTenantId).
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only").Options);
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is not null && table.StartsWith("platform_", StringComparison.Ordinal))
            {
                Assert.Null(entity.FindProperty("TenantId"));
            }
        }
    }
}
