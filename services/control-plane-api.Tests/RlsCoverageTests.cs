using ControlPlane.Api.Migrations;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Migration gate (ADR 0018 §4): every tenant-owned table must be covered by the
/// Row-Level Security migration. A new tenant table cannot ship without isolation.
///
/// The gate forces every mapped table into exactly one bucket:
///   • carries a <c>TenantId</c> column        ⇒ must have an RLS policy;
///   • listed in <see cref="JoinScopedTables"/> ⇒ tenant-owned via a documented
///     join/self policy ⇒ must have an RLS policy;
///   • listed in <see cref="GlobalTables"/>     ⇒ global/user-scoped ⇒ must NOT
///     have a tenant RLS policy and must NOT carry a TenantId.
/// Anything else is "unclassified" and fails the build, forcing a deliberate
/// decision rather than a silent isolation gap.
/// </summary>
public sealed class RlsCoverageTests
{
    // Global identity/account tables — intentionally NOT tenant-scoped; protected
    // by the app's user-id checks (ADR 0018 §3). Adding a table here is a
    // deliberate, reviewed decision.
    private static readonly HashSet<string> GlobalTables = new()
    {
        "users", "user_sessions", "user_tokens", "recovery_codes",
    };

    // Tenant-owned tables that carry no TenantId column and are covered by a
    // documented join/self policy instead (ADR 0018 §3).
    private static readonly HashSet<string> JoinScopedTables = new()
    {
        "device_credentials", // keyed by GatewayId -> gateways.TenantId
        "tenants",            // the tenant row itself (Id = app.tenant_id)
    };

    // Build the model with the real relational provider so ToTable(...) names
    // resolve. UseNpgsql only configures metadata — it opens no connection.
    private static AppDbContext RelationalModelContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .Options);

    private static HashSet<string> CoveredTables() =>
        AddRowLevelSecurity.Policies.Select(p => p.Table).ToHashSet();

    [Fact]
    public void Every_Table_Is_Classified_And_Tenant_Tables_Have_Rls()
    {
        using var db = RelationalModelContext();
        var covered = CoveredTables();
        var problems = new List<string>();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null)
            {
                continue; // owned/keyless type with no standalone table
            }

            var hasTenantId = entity.FindProperty("TenantId") is not null;

            if (GlobalTables.Contains(table))
            {
                if (covered.Contains(table))
                {
                    problems.Add($"{table}: listed global but has a tenant RLS policy");
                }
                if (hasTenantId)
                {
                    problems.Add($"{table}: listed global but carries a TenantId column");
                }
                continue;
            }

            var tenantOwned = hasTenantId || JoinScopedTables.Contains(table);
            if (!tenantOwned)
            {
                problems.Add(
                    $"{table}: unclassified — give it a TenantId + RLS policy, or list it in " +
                    "JoinScopedTables (with a join/self policy), or in GlobalTables if genuinely global");
                continue;
            }

            if (!covered.Contains(table))
            {
                problems.Add($"{table}: tenant-owned but missing an RLS policy in AddRowLevelSecurity");
            }
        }

        Assert.True(problems.Count == 0, "RLS migration-gate failures:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    public void Every_Rls_Policy_Targets_A_Real_Non_Global_Table()
    {
        using var db = RelationalModelContext();
        var tables = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(t => t is not null)
            .ToHashSet();

        foreach (var (table, _) in AddRowLevelSecurity.Policies)
        {
            Assert.True(tables.Contains(table), $"RLS policy targets unknown table '{table}'");
            Assert.False(GlobalTables.Contains(table), $"RLS policy must not tenant-scope global table '{table}'");
        }
    }

    [Fact]
    public void All_Ten_Known_Tenant_Tables_Are_Covered()
    {
        // Explicit lock on the P1 coverage set so an accidental removal is caught.
        var covered = CoveredTables();
        string[] expected =
        {
            "gateways", "bootstrap_tokens", "configs", "audit", "memberships",
            "invitations", "subscriptions", "billing_events", "device_credentials", "tenants",
        };
        foreach (var table in expected)
        {
            Assert.True(covered.Contains(table), $"expected RLS coverage for '{table}'");
        }
        Assert.Equal(expected.Length, covered.Count);
    }
}
