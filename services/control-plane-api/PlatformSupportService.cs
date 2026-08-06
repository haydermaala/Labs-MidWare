// Platform support-access grant flow (P6, program prompt §7.2/§9). A platform support
// engineer has NO standing tenant-data access; they REQUEST a time-limited grant to a
// tenant, and a DISTINCT approver (Security) approves it — dynamic separation of duty
// (a user may not approve their own support-access request,
// SeparationOfDuty.IsDistinctParty). Access is live only while approved and unexpired.
//
// GLOBAL/platform, like the platform role assignments: no TenantId column (the tenant
// is SubjectTenantId), not under RLS.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Requests, decides, and resolves time-limited tenant support-access grants.</summary>
public sealed class PlatformSupportService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public PlatformSupportService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public enum DecideOutcome { Ok, NotFound, NotPending, SameParty }

    /// <summary>Open a pending support-access request for a tenant. Entitlement to
    /// request is checked by the caller (platform.support.request).</summary>
    /// <summary>Default grant length when the caller does not choose one.</summary>
    public const int DefaultDurationMinutes = 60;

    /// <summary>
    /// Hard ceiling on a support grant, one working shift.
    ///
    /// The duration was previously floored (<c>&lt;= 0 ? 60 : n</c>) but never capped, so
    /// a caller could request a grant lasting years. That matters because a grant is
    /// cross-tenant access to someone else's data: an unbounded one is indistinguishable
    /// from standing access, which is the thing support grants exist to replace.
    ///
    /// A longer incident is not blocked, it is re-requested — and re-requesting puts it
    /// back through two-party approval, which is the control we actually want applied
    /// repeatedly rather than waived once at the start.
    /// </summary>
    public const int MaxDurationMinutes = 8 * 60;

    private static int ClampDuration(int durationMinutes) =>
        durationMinutes <= 0 ? DefaultDurationMinutes : Math.Min(durationMinutes, MaxDurationMinutes);

    public PlatformSupportGrantView Request(
        string subjectTenantId, string requesterUserId, string reason, int durationMinutes)
    {
        using var db = _factory.CreateDbContext();
        var entity = new PlatformSupportGrantEntity
        {
            Id = Ids.New("psg"),
            SubjectTenantId = subjectTenantId,
            RequesterUserId = requesterUserId,
            Reason = reason,
            RequestedDurationMinutes = ClampDuration(durationMinutes),
            Status = SupportGrantStatus.Pending,
            CreatedAt = _clock.GetUtcNow(),
        };
        db.PlatformSupportGrants.Add(entity);
        db.SaveChanges();
        return ToView(entity, _clock.GetUtcNow());
    }

    /// <summary>Approve a pending request — fails if unknown, already decided, or the
    /// approver is the requester (dynamic SoD). On approval the grant becomes live and
    /// expires at now + the requested duration.</summary>
    public DecideOutcome Approve(string grantId, string approverUserId)
    {
        using var db = _factory.CreateDbContext();
        var grant = db.PlatformSupportGrants.FirstOrDefault(g => g.Id == grantId);
        if (grant is null)
        {
            return DecideOutcome.NotFound;
        }
        if (grant.Status != SupportGrantStatus.Pending)
        {
            return DecideOutcome.NotPending;
        }
        if (!SeparationOfDuty.IsDistinctParty(approverUserId, grant.RequesterUserId))
        {
            return DecideOutcome.SameParty;
        }
        var now = _clock.GetUtcNow();
        grant.Status = SupportGrantStatus.Approved;
        grant.DecidedByUserId = approverUserId;
        grant.DecidedAt = now;
        grant.ExpiresAt = now.AddMinutes(grant.RequestedDurationMinutes);
        db.SaveChanges();
        return DecideOutcome.Ok;
    }

    /// <summary>Reject a pending request (no distinct-party rule — an approver may
    /// decline, and a requester may cancel).</summary>
    public DecideOutcome Reject(string grantId, string deciderUserId)
    {
        using var db = _factory.CreateDbContext();
        var grant = db.PlatformSupportGrants.FirstOrDefault(g => g.Id == grantId);
        if (grant is null)
        {
            return DecideOutcome.NotFound;
        }
        if (grant.Status != SupportGrantStatus.Pending)
        {
            return DecideOutcome.NotPending;
        }
        grant.Status = SupportGrantStatus.Rejected;
        grant.DecidedByUserId = deciderUserId;
        grant.DecidedAt = _clock.GetUtcNow();
        db.SaveChanges();
        return DecideOutcome.Ok;
    }

    /// <summary>Whether the user currently holds live (approved, unexpired) support
    /// access to the tenant — the check a support-scoped tenant read would make.</summary>
    public bool HasActiveGrant(string subjectTenantId, string userId)
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        return db.PlatformSupportGrants.AsNoTracking().Any(g =>
            g.SubjectTenantId == subjectTenantId && g.RequesterUserId == userId &&
            g.Status == SupportGrantStatus.Approved && g.ExpiresAt != null && g.ExpiresAt > now);
    }

    /// <summary>Pending support-access requests, oldest first.</summary>
    public IReadOnlyList<PlatformSupportGrantView> Pending()
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        return db.PlatformSupportGrants.AsNoTracking()
            .Where(g => g.Status == SupportGrantStatus.Pending)
            .OrderBy(g => g.CreatedAt)
            .AsEnumerable()
            .Select(g => ToView(g, now))
            .ToList();
    }

    private static PlatformSupportGrantView ToView(PlatformSupportGrantEntity g, DateTimeOffset now) =>
        new(g.Id, g.SubjectTenantId, g.RequesterUserId, g.Reason, g.Status, g.CreatedAt, g.ExpiresAt,
            g.DecidedByUserId,
            g.Status == SupportGrantStatus.Approved && g.ExpiresAt is not null && g.ExpiresAt > now);
}

/// <summary>A support-access grant as returned by the platform API.</summary>
public sealed record PlatformSupportGrantView(
    string Id, string SubjectTenantId, string RequesterUserId, string Reason, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? DecidedByUserId, bool Active);
