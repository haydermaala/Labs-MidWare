using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Provider-parity guard.
///
/// The functional suites run on the EF in-memory provider, which evaluates any
/// LINQ shape client-side. Deployments run on Npgsql, which must translate the
/// query to SQL — so a query can pass every functional test and still fail with
/// a 500 in production. `ToQueryString()` forces translation **without opening a
/// connection**, turning that class of bug into a fast unit test.
///
/// This exists because `MembersOf` shipped an untranslatable `OrderBy` over a
/// projected record and only failed once it hit real Postgres.
/// </summary>
public sealed class QueryTranslationTests
{
    private static AppDbContext RelationalContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            // Never connected to; ToQueryString only needs the provider's SQL generator.
            .UseNpgsql("Host=localhost;Database=translation-check;Username=none;Password=none")
            .Options);

    [Fact]
    public void Members_Query_Translates_To_Sql()
    {
        using var db = RelationalContext();
        var sql = MembershipService.MembersQuery(db, "ten_1").ToQueryString();
        Assert.Contains("memberships", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tenant_Scoped_Reads_Translate_To_Sql()
    {
        using var db = RelationalContext();

        // Each of these backs a live endpoint; an untranslatable shape here is a
        // production 500 regardless of what the in-memory suites report.
        Assert.Contains("gateways", db.Gateways.AsNoTracking()
            .Where(g => g.TenantId == "ten_1").OrderBy(g => g.EnrolledAt)
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("audit", db.Audit.AsNoTracking()
            .Where(e => e.TenantId == "ten_1").OrderBy(e => e.At).ThenBy(e => e.Id)
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("invitations", db.Invitations.AsNoTracking()
            .Where(i => i.TenantId == "ten_1").OrderByDescending(i => i.CreatedAt)
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("user_sessions", db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == "usr_1" && s.RevokedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("memberships", db.Memberships.AsNoTracking()
            .Where(m => m.UserId == "usr_1" && m.Active)
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);

        // FindTenant projects to a record constructor; guard it translates.
        Assert.Contains("tenants", db.Tenants.AsNoTracking()
            .Where(t => t.Id == "ten_1")
            .Select(t => new Tenant(t.Id, t.Name, t.CreatedAt, t.Active))
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);

        // Billing reads behind the entitlements/subscription endpoints.
        Assert.Contains("subscriptions", db.Subscriptions.AsNoTracking()
            .Where(s => s.TenantId == "ten_1")
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("billing_events", db.BillingEvents.AsNoTracking()
            .Where(b => b.ProviderEventId == "evt_1")
            .ToQueryString(), StringComparison.OrdinalIgnoreCase);
    }
}
