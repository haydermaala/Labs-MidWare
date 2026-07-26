// Transaction-scoped context for accepting an invitation (ADR 0018 §8). An
// invitation is redeemed by presenting its (hashed) token before the tenant is
// known — reading it to validate is what tenant RLS would block. This helper
// binds the transaction-local `app.invitation_token_hash` that the
// `invitations_token_auth` policy checks (revealing only the matching invitation),
// then BindTenant once the invitation's tenant is resolved so the membership
// write passes the tenant policies. Mirrors DeviceScope for bootstrap tokens.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlPlane.Api;

/// <summary>
/// Binds <c>app.invitation_token_hash</c> to prove possession of an invitation
/// token, then optionally <see cref="BindTenant"/> to the resolved tenant for the
/// accept write. No-op under non-PostgreSQL providers (tests), so behaviour there
/// is unchanged. The hash flows as a bound parameter, never interpolated into SQL.
/// </summary>
internal sealed class InvitationScope : IDisposable
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly AppDbContext _db;
    private readonly IDbContextTransaction? _tx;
    private bool _completed;

    private InvitationScope(AppDbContext db, IDbContextTransaction? tx)
    {
        _db = db;
        _tx = tx;
    }

    public static InvitationScope Begin(AppDbContext db, string tokenHash)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
        {
            return new InvitationScope(db, null);
        }

        var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSql($"SELECT set_config('app.invitation_token_hash', {tokenHash}, true)");
        return new InvitationScope(db, tx);
    }

    /// <summary>Bind <c>app.tenant_id</c> to the resolved tenant so the membership
    /// write and the invitation update pass the tenant policies.</summary>
    public void BindTenant(string tenantId)
    {
        if (_tx is null)
        {
            return;
        }

        _db.Database.ExecuteSql($"SELECT set_config('app.tenant_id', {tenantId}, true)");
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
