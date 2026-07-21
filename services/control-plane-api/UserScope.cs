// Transaction-scoped user context for a self-read that spans tenants (ADR 0018
// §8). A signed-in user may read their OWN memberships across every tenant they
// belong to (the tenant switcher), which cannot run under a single tenant GUC.
// This helper binds the transaction-local `app.user_id` that the
// `memberships_self_read` policy checks — a read of the caller's own rows only.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlPlane.Api;

/// <summary>
/// Binds <c>app.user_id</c> for one operation so the memberships self-policy
/// permits the caller to read their own memberships across tenants. The value is
/// the verified session user id, never a request field.
///
/// No-op under non-PostgreSQL providers (the in-memory provider has no RLS), so
/// existing behaviour and tests are unchanged.
/// </summary>
internal sealed class UserScope : IDisposable
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly IDbContextTransaction? _tx;
    private bool _completed;

    private UserScope(IDbContextTransaction? tx) => _tx = tx;

    public static UserScope Begin(AppDbContext db, string userId)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
        {
            return new UserScope(null);
        }

        var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSql($"SELECT set_config('app.user_id', {userId}, true)");
        return new UserScope(tx);
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
