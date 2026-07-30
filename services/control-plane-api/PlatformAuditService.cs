// Platform security/audit event log (P6, program prompt §8). Records privileged
// platform operations (role grants/revokes, tenant lifecycle, support approvals) so
// the Security/Auditor roles can review them. Append-only; GLOBAL/platform (no
// TenantId, not under RLS).

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Records and reads platform security events.</summary>
public sealed class PlatformAuditService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public PlatformAuditService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    /// <summary>Append a security event for a privileged platform operation.</summary>
    public void Record(string kind, string actorUserId, string detail)
    {
        using var db = _factory.CreateDbContext();
        db.PlatformSecurityEvents.Add(new PlatformSecurityEventEntity
        {
            Id = Ids.New("pse"),
            At = _clock.GetUtcNow(),
            Kind = kind,
            ActorUserId = actorUserId,
            Detail = detail,
        });
        db.SaveChanges();
    }

    /// <summary>The most recent platform security events, newest first.</summary>
    public IReadOnlyList<PlatformSecurityEventView> Recent(int limit = 100)
    {
        using var db = _factory.CreateDbContext();
        return db.PlatformSecurityEvents.AsNoTracking()
            .OrderByDescending(e => e.At)
            .Take(limit <= 0 ? 100 : limit)
            .AsEnumerable()
            .Select(e => new PlatformSecurityEventView(e.Id, e.At, e.Kind, e.ActorUserId, e.Detail))
            .ToList();
    }
}

/// <summary>A platform security event as returned by the platform API.</summary>
public sealed record PlatformSecurityEventView(
    string Id, DateTimeOffset At, string Kind, string ActorUserId, string Detail);
