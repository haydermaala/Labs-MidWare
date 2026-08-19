using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Tests;

/// <summary>
/// The god-mode admin token is retired on the evidence of one question: "has it been
/// used in this window?" scripts/break-glass-watch.sh answers that by querying
/// platform_security_events for `platform.break_glass.used`.
///
/// That answer is only as complete as the recording. A path that ACCEPTS the token
/// without recording makes the gate report a false all-clear — which is strictly worse
/// than having no gate, because it is a confident wrong answer to "is it safe to
/// withdraw the credential that is my only way back into production?"
///
/// This happened. PlatformForbidden short-circuited on the token and recorded nothing,
/// so the whole /api/platform/* surface was invisible to the gate; verified against
/// production, three successful platform calls produced zero events. These tests exist
/// so it cannot happen again.
/// </summary>
public sealed class BreakGlassAuditTests : IClassFixture<IsolatedApiFactory>
{
    private readonly IsolatedApiFactory _factory;

    public BreakGlassAuditTests(IsolatedApiFactory factory) => _factory = factory;

    private sealed record SecurityEventDto(string Id, DateTimeOffset At, string Kind, string ActorUserId, string Detail);
    private sealed record TenantDto(string Id, string Name);
    private sealed record UserDto(string Id, string Email);
    private sealed record LoginDto(string SessionToken, UserDto User);

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

    // An auditor session, NOT the token. This is load-bearing, not hygiene.
    //
    // The obvious way to write these tests is to read the trail with the admin client.
    // That makes every one of them vacuous: reading /api/platform/security-events goes
    // through PlatformForbidden, which records a break-glass event before the handler
    // runs. So the "before" read and the "after" read each add one, and
    // `after.Count > before` holds no matter what the endpoint under test does.
    //
    // It is the same trap that makes scripts/break-glass-watch.sh unable to return
    // "ready": an instrument that authenticates with the credential it is measuring
    // perturbs its own measurement. The observer must be a different principal.
    private HttpClient? _auditor;

    private async Task<HttpClient> Auditor()
    {
        if (_auditor is not null) return _auditor;

        var email = $"auditor-{Guid.NewGuid():N}@example.test";
        const string password = "correct horse battery staple";
        var created = await Admin().PostAsJsonAsync("/api/platform/users", new { email, password });
        var user = (await created.Content.ReadFromJsonAsync<UserDto>())!;
        await Admin().PostAsJsonAsync("/api/platform/role-assignments",
            new { userId = user.Id, role = PlatformRoles.Auditor });
        var login = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login", new { email, password });
        var session = (await login.Content.ReadFromJsonAsync<LoginDto>())!.SessionToken;
        return _auditor = Session(session);
    }

    private async Task<List<SecurityEventDto>> BreakGlassEvents()
    {
        var all = await (await Auditor()).GetFromJsonAsync<List<SecurityEventDto>>(
            "/api/platform/security-events?limit=500");
        return all!.Where(e => e.Kind == "platform.break_glass.used").ToList();
    }

    [Fact]
    public async Task Observing_The_Trail_Does_Not_Add_To_It()
    {
        // The control for every other test in this file. Two observations with NOTHING
        // in between must agree. If the observer records, this fails — and every
        // "did X record?" assertion elsewhere would have been passing on the observer's
        // own footprints rather than on X.
        //
        // This is also the precise defect in scripts/break-glass-watch.sh: it reads the
        // trail using the admin token, so each run writes a break-glass event into the
        // window it is about to measure and can never report a quiet window.
        var first = (await BreakGlassEvents()).Count;
        var second = (await BreakGlassEvents()).Count;

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Platform_Gate_Records_Break_Glass()
    {
        // The regression that motivated this file: PlatformForbidden returned null for
        // the token at its first statement, so no event was written for any platform
        // permission — including the Critical, Root-Owner-only ones.
        var before = (await BreakGlassEvents()).Count;

        var res = await Admin().GetAsync("/api/platform/tenants");
        res.EnsureSuccessStatusCode();

        var after = await BreakGlassEvents();
        Assert.True(after.Count > before,
            "a successful /api/platform/* call with the admin token recorded no " +
            "break-glass event; the retirement gate is blind to this surface");
        Assert.Contains(after, e => e.Detail.StartsWith("platform:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tenant_Gate_Records_Break_Glass_With_Tenant_And_Permission()
    {
        var tenant = (await (await Admin().PostAsJsonAsync("/api/tenants", new { name = "BG Co" }))
            .Content.ReadFromJsonAsync<TenantDto>())!.Id;

        (await Admin().GetAsync($"/api/tenants/{tenant}/gateways")).EnsureSuccessStatusCode();

        var events = await BreakGlassEvents();
        Assert.Contains(events, e => e.Detail == $"{tenant}:{Permissions.FleetGatewayView.Key}");
    }

    [Fact]
    public async Task Bootstrap_Endpoints_Record_Break_Glass()
    {
        // The direct-guard endpoints have no Forbidden/PlatformForbidden gate at all,
        // so they are the easiest ones to leave unrecorded.
        var before = (await BreakGlassEvents()).Count;

        await Admin().PostAsJsonAsync("/api/tenants", new { name = "Bootstrap Co" });
        (await Admin().GetAsync("/api/tenants")).EnsureSuccessStatusCode();

        var after = await BreakGlassEvents();
        Assert.True(after.Count >= before + 2,
            "tenant.create and tenant.list are token-only paths and must each record");
        Assert.Contains(after, e => e.Detail == "tenant.create");
        Assert.Contains(after, e => e.Detail == "tenant.list");
    }

    [Fact]
    public async Task Break_Glass_Actor_Is_Always_Attributed()
    {
        (await Admin().GetAsync("/api/platform/tenants")).EnsureSuccessStatusCode();

        var events = await BreakGlassEvents();
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal("platform-admin", e.ActorUserId));
        Assert.All(events, e => Assert.False(string.IsNullOrWhiteSpace(e.Detail)));
    }

    // ---- structural guards -------------------------------------------------
    //
    // The behavioural tests above sample a few endpoints. These enforce the invariant
    // across the whole service, because sampling is what let the original defect ship:
    // Forbidden was fixed and its twin PlatformForbidden was not, and nothing noticed.

    private static string ServiceRoot() =>
        Path.Combine(RepoRoot(), "services", "control-plane-api");

    /// <summary>Every non-generated C# file in the service.</summary>
    private static IEnumerable<string> ServiceSources() =>
        Directory.EnumerateFiles(ServiceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>
    /// `BreakGlass(req, context)` both accepts the token and records the use, so it is the
    /// only sanctioned way to ask "is this the token?" at an authorization site. Raw
    /// `IsAdmin` has exactly three legitimate uses, all in Program.cs and all listed here.
    ///
    /// Scans the WHOLE service, not just Program.cs — the earlier version only read
    /// Program.cs and so could not have seen a gate added in a new file.
    /// </summary>
    [Fact]
    public void No_Unaudited_IsAdmin_Call_Sites()
    {
        var offenders = new List<string>();

        foreach (var file in ServiceSources())
        {
            var lines = File.ReadAllLines(file);
            var name = Path.GetFileName(file);
            var isProgram = name == "Program.cs";

            for (var i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"\bIsAdmin\s*\(")) continue;
                if (Regex.IsMatch(lines[i], @"bool\s+IsAdmin\s*\(")) continue; // the definition

                if (isProgram)
                {
                    // Inside BreakGlass — the recording wrapper itself.
                    if (lines[i].Contains("if (!IsAdmin(req))", StringComparison.Ordinal)
                        && Within(lines, i, "bool BreakGlass(", 12)) continue;
                    // ActorRole — its callers already passed Forbidden on this request,
                    // which recorded. Recording again double-counts one use. That premise
                    // is enforced by Every_ActorRole_Caller_Has_Already_Passed_Forbidden.
                    if (Within(lines, i, "string ActorRole(", 12)) continue;
                    // CurrentUser — an identity question, not an authorization site.
                    if (Within(lines, i, "? CurrentUser(HttpRequest", 22)) continue;
                }

                offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these sites accept the admin token without recording a break-glass event, "
            + "which makes scripts/break-glass-watch.sh report a false all-clear. Use "
            + "BreakGlass(req, context) instead of IsAdmin(req):\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The other way to bypass the wrapper: skip `IsAdmin` and compare the header to the
    /// captured token yourself. `adminToken` is a local in a top-level program, so it is
    /// in scope for the entire file and an inline `== $"Bearer {adminToken}"` would
    /// authorize without recording — invisible to the call-site scan above.
    ///
    /// So the token VALUE may be touched in exactly two places: where it is read from
    /// configuration, and inside IsAdmin.
    /// </summary>
    [Fact]
    public void No_Inline_Reimplementation_Of_The_Token_Check()
    {
        var offenders = new List<string>();

        foreach (var file in ServiceSources())
        {
            var lines = File.ReadAllLines(file);
            var name = Path.GetFileName(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], @"\badminToken\b")) continue;
                if (lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                // The single read from configuration.
                if (Regex.IsMatch(lines[i], @"var\s+adminToken\s*=")) continue;
                // The body of IsAdmin.
                if (Within(lines, i, "bool IsAdmin(", 4)) continue;

                offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "the admin token value is referenced outside its declaration and IsAdmin. An "
            + "inline header comparison authorizes without recording a break-glass event, "
            + "which the call-site scan cannot see. Route it through BreakGlass:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// ActorRole is exempt from the recording rule only because every caller has already
    /// passed Forbidden on the same request, which recorded the use. That was asserted in
    /// a comment and enforced by nothing. It is enforced here.
    /// </summary>
    [Fact]
    public void Every_ActorRole_Caller_Has_Already_Passed_Forbidden()
    {
        var lines = File.ReadAllLines(Path.Combine(ServiceRoot(), "Program.cs"));
        var offenders = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            if (!Regex.IsMatch(lines[i], @"\bActorRole\s*\(\s*req\b")) continue;
            if (Within(lines, i, "string ActorRole(", 12)) continue; // the definition

            // Walk back to the top of the enclosing endpoint registration and require a
            // Forbidden(req, …) gate somewhere between it and this call.
            var gated = false;
            for (var j = i; j >= 0; j--)
            {
                if (Regex.IsMatch(lines[j], @"\bForbidden\s*\(\s*req\b")) { gated = true; break; }
                if (Regex.IsMatch(lines[j], @"app\.Map(Get|Post|Put|Delete|Patch)\s*\(")) break;
            }
            if (!gated) offenders.Add($"Program.cs:{i + 1}: {lines[i].Trim()}");
        }

        Assert.NotEmpty(
            lines.Where((l, i) => Regex.IsMatch(l, @"\bActorRole\s*\(\s*req\b")).ToList());
        Assert.True(offenders.Count == 0,
            "ActorRole returns Owner for the admin token WITHOUT recording, which is only "
            + "safe because the caller already passed Forbidden on the same request. These "
            + "call sites have no such gate, so the token reaches Owner unrecorded:\n  "
            + string.Join("\n  ", offenders));
    }

    private static bool Within(string[] lines, int index, string header, int lookback)
    {
        for (var j = index; j >= Math.Max(0, index - lookback); j--)
        {
            if (lines[j].Contains(header, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
