// Transaction-scoped platform context for a trusted cross-tenant read of the
// tenant registry (ADR 0018 §7). Some server-side operations legitimately span
// tenants — the admin tenant list, and resolving tenant names across a user's
// memberships — and so cannot run under a single tenant GUC. This helper binds
// the transaction-local `app.platform` flag that the `tenants_platform_read`
// policy checks, granting a read of the tenant registry only (never tenant DATA).

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlPlane.Api;

/// <summary>
/// Binds <c>app.platform = 'true'</c> for one operation so the platform policy on
/// <c>tenants</c> permits a cross-tenant read of the registry. Only trusted
/// server-side, already-authorised operations open this scope; single-tenant
/// lookups stay tenant-scoped (least privilege).
///
/// No-op under non-PostgreSQL providers (the in-memory provider has no RLS), so
/// existing behaviour and tests are unchanged.
/// </summary>
internal sealed class PlatformScope : IDisposable
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly IDbContextTransaction? _tx;
    private bool _completed;

    private PlatformScope(IDbContextTransaction? tx) => _tx = tx;

    public static PlatformScope Begin(AppDbContext db)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
        {
            return new PlatformScope(null);
        }

        var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSql($"SELECT set_config('app.platform', 'true', true)");
        return new PlatformScope(tx);
    }

    public void Complete()
    {
        _tx?.Commit();
        _completed = true;
    }

    public void Dispose()
    {
        if (_tx is null)
        {
            return;
        }

        if (!_completed)
        {
            _tx.Rollback();
        }

        _tx.Dispose();
    }
}
