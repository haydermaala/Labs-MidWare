// Two-party tenant offboarding flow (P6/P7, program prompt §9/§10.3). Offboarding a
// tenant is destructive, so — like a support-access grant — a requester opens it and a
// DISTINCT approver executes it (a user may not approve their own tenant offboarding
// request, SeparationOfDuty.IsDistinctParty). On approval the endpoint BEGINS the
// offboarding pipeline (IControlPlaneStore.TransitionTenant → offboarding), which is
// cancellable during cooling-off and completed by a separate archive step.
//
// GLOBAL/platform: the tenant reference is SubjectTenantId (not TenantId), so this is
// not tenant-RLS-scoped — it is a platform artifact gated by platform authz.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Requests and decides two-party tenant offboarding.</summary>
public sealed class PlatformOffboardService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public PlatformOffboardService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public enum DecideOutcome { Ok, NotFound, NotPending, SameParty }

    /// <summary>Open a pending offboarding request for a tenant.</summary>
    public PlatformOffboardRequestView Request(string subjectTenantId, string requesterUserId, string reason)
    {
        using var db = _factory.CreateDbContext();
        var entity = new PlatformOffboardRequestEntity
        {
            Id = Ids.New("pof"),
            SubjectTenantId = subjectTenantId,
            RequesterUserId = requesterUserId,
            Reason = reason,
            Status = ApprovalStatus.Pending,
            CreatedAt = _clock.GetUtcNow(),
        };
        db.PlatformOffboardRequests.Add(entity);
        db.SaveChanges();
        return ToView(entity);
    }

    /// <summary>The tenant an approved-if-distinct request targets, so the caller can
    /// execute the offboarding. Returns (Ok, tenantId) only when the approver differs
    /// from the requester and the request is pending; otherwise the failure outcome.</summary>
    public (DecideOutcome Outcome, string? SubjectTenantId) Approve(string requestId, string approverUserId)
    {
        using var db = _factory.CreateDbContext();
        var request = db.PlatformOffboardRequests.FirstOrDefault(r => r.Id == requestId);
        if (request is null)
        {
            return (DecideOutcome.NotFound, null);
        }
        if (request.Status != ApprovalStatus.Pending)
        {
            return (DecideOutcome.NotPending, null);
        }
        if (!SeparationOfDuty.IsDistinctParty(approverUserId, request.RequesterUserId))
        {
            return (DecideOutcome.SameParty, null);
        }
        request.Status = ApprovalStatus.Approved;
        request.DecidedByUserId = approverUserId;
        request.DecidedAt = _clock.GetUtcNow();
        db.SaveChanges();
        return (DecideOutcome.Ok, request.SubjectTenantId);
    }

    /// <summary>Reject a pending offboarding request.</summary>
    public DecideOutcome Reject(string requestId, string deciderUserId)
    {
        using var db = _factory.CreateDbContext();
        var request = db.PlatformOffboardRequests.FirstOrDefault(r => r.Id == requestId);
        if (request is null)
        {
            return DecideOutcome.NotFound;
        }
        if (request.Status != ApprovalStatus.Pending)
        {
            return DecideOutcome.NotPending;
        }
        request.Status = ApprovalStatus.Rejected;
        request.DecidedByUserId = deciderUserId;
        request.DecidedAt = _clock.GetUtcNow();
        db.SaveChanges();
        return DecideOutcome.Ok;
    }

    /// <summary>Pending offboarding requests, oldest first.</summary>
    public IReadOnlyList<PlatformOffboardRequestView> Pending()
    {
        using var db = _factory.CreateDbContext();
        return db.PlatformOffboardRequests.AsNoTracking()
            .Where(r => r.Status == ApprovalStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .AsEnumerable()
            .Select(ToView)
            .ToList();
    }

    private static PlatformOffboardRequestView ToView(PlatformOffboardRequestEntity r) =>
        new(r.Id, r.SubjectTenantId, r.RequesterUserId, r.Reason, r.Status, r.CreatedAt, r.DecidedByUserId);
}

/// <summary>A tenant offboarding request as returned by the platform API.</summary>
public sealed record PlatformOffboardRequestView(
    string Id, string SubjectTenantId, string RequesterUserId, string Reason, string Status,
    DateTimeOffset CreatedAt, string? DecidedByUserId);
