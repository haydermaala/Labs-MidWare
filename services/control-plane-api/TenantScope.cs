// Transaction-scoped tenant context for a single store operation (ADR 0018, P1).
//
// Opens a DB transaction and binds the `app.tenant_id` GUC that the Row-Level
// Security policies read via `current_setting('app.tenant_id', true)`. This makes
// the database itself refuse cross-tenant reads/writes — defense-in-depth beneath
// the application-layer `tenant_id` filters, which stay in place.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlPlane.Api;

/// <summary>
/// Binds <c>app.tenant_id</c> for one store operation. Begin a scope, run the
/// operation, then <see cref="Complete"/> it; disposing without completing rolls
/// the transaction back.
///
/// The GUC is set with <c>set_config(name, value, is_local := true)</c> — the
/// function form of <c>SET LOCAL</c>, so it is transaction-scoped and never leaks
/// across pooled connections, and it takes the tenant id as a bound parameter
/// (never string-interpolated into SQL).
///
/// No-op under non-PostgreSQL providers: the in-memory provider used by tests has
/// no RLS and does not support transactions or raw SQL, so this changes nothing
/// there and all existing behaviour/tests are preserved.
/// </summary>
internal sealed class TenantScope : IDisposable
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly IDbContextTransaction? _tx;
    private bool _completed;

    private TenantScope(IDbContextTransaction? tx) => _tx = tx;

    /// <summary>
    /// Begin a scope binding <c>app.tenant_id</c> to <paramref name="tenantId"/>.
    /// The caller is responsible for having authorised access to that tenant; the
    /// scope is the database-level enforcement of that decision, not the decision.
    /// </summary>
    public static TenantScope Begin(AppDbContext db, string tenantId)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
        {
            return new TenantScope(null);
        }

        var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSql($"SELECT set_config('app.tenant_id', {tenantId}, true)");
        return new TenantScope(tx);
    }

    /// <summary>Commit the operation's transaction. Call after SaveChanges / after
    /// materialising query results.</summary>
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
