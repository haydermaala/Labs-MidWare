// Transaction-scoped device-auth context for a single device-plane operation
// (ADR 0018 §6). A gateway authenticates by proving possession of a secret — a
// bootstrap token (enrollment) or a device credential (steady state) — before its
// tenant is known. This helper begins a transaction and binds the transaction-
// local GUCs the device-auth RLS policies read, so the database reveals only the
// row whose secret the caller presented.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlPlane.Api;

/// <summary>
/// Binds the device-auth GUCs for one operation. Begin a scope, run the read that
/// proves possession, optionally <see cref="BindTenant"/> once the tenant is
/// resolved (so subsequent writes pass the tenant policies), then
/// <see cref="Complete"/>. Disposing without completing rolls the transaction back.
///
/// No-op under non-PostgreSQL providers (the in-memory provider has no RLS and no
/// transaction/raw-SQL support), so existing behaviour and tests are unchanged.
/// Values flow as bound parameters, never string-interpolated into SQL.
/// </summary>
internal sealed class DeviceScope : IDisposable
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private readonly AppDbContext _db;
    private readonly IDbContextTransaction? _tx;
    private bool _completed;

    private DeviceScope(AppDbContext db, IDbContextTransaction? tx)
    {
        _db = db;
        _tx = tx;
    }

    /// <summary>Enrollment: prove possession of a bootstrap token (`app.device_token`).</summary>
    public static DeviceScope ForEnrollment(AppDbContext db, string bootstrapToken)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
        {
            return new DeviceScope(db, null);
        }

        var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSql($"SELECT set_config('app.device_token', {bootstrapToken}, true)");
        return new DeviceScope(db, tx);
    }

    /// <summary>Steady state: prove possession of a gateway's device credential
    /// (`app.device_gateway` + `app.device_credential`).</summary>
    public static DeviceScope ForCredential(AppDbContext db, string gatewayId, string credential)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
        {
            return new DeviceScope(db, null);
        }

        var tx = db.Database.BeginTransaction();
        db.Database.ExecuteSql(
            $"SELECT set_config('app.device_gateway', {gatewayId}, true), set_config('app.device_credential', {credential}, true)");
        return new DeviceScope(db, tx);
    }

    /// <summary>Bind <c>app.tenant_id</c> to the resolved tenant so writes in the
    /// same transaction pass the tenant policies (used after reading the token/
    /// credential row to learn the tenant).</summary>
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
