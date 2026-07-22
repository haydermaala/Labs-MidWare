// Two-party approval flow for approval-gated actions (P3, ADR 0020 §5 — dynamic
// separation of duty). A permission whose catalog entry sets RequiresApproval can
// only take effect after a DISTINCT second authorized party approves it: the actor
// who requests an action may not be the one who approves it
// (SeparationOfDuty.IsDistinctParty). This service owns the request lifecycle
// (pending → approved/rejected); the caller's *entitlement* to request or approve
// is checked separately by the authorization gate, and the action itself is
// executed by the endpoint once approved.
//
// EF-backed like the other P3 services, app-layer tenant-filtered. RLS policies
// for approval_requests land at the P1+P3 merge.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Creates and decides two-party approval requests, enforcing that the
/// approver is a distinct party from the requester.</summary>
public sealed class ApprovalService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public ApprovalService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public enum DecideOutcome { Ok, NotFound, NotPending, SameParty }

    /// <summary>Create a pending approval request. The permission must exist and be
    /// approval-gated; entitlement to request it is checked by the caller.</summary>
    public ApprovalRequestView? Create(
        string tenantId, string permissionKey, string? scopeId, string requesterUserId,
        string? targetId, string? note)
    {
        var permission = Permissions.Find(permissionKey);
        if (permission is null || !permission.RequiresApproval)
        {
            return null;
        }
        using var db = _factory.CreateDbContext();
        var entity = new ApprovalRequestEntity
        {
            Id = Ids.New("apr"),
            TenantId = tenantId,
            PermissionKey = permissionKey,
            ScopeId = scopeId,
            TargetId = targetId,
            RequesterUserId = requesterUserId,
            Note = note,
            Status = ApprovalStatus.Pending,
            CreatedAt = _clock.GetUtcNow(),
        };
        db.ApprovalRequests.Add(entity);
        db.SaveChanges();
        return ToView(entity);
    }

    /// <summary>The pending request in the tenant, or null if unknown — used by the
    /// endpoint to resolve the permission/scope it must authorize the approver for.</summary>
    public ApprovalRequestEntity? Find(string tenantId, string requestId)
    {
        using var db = _factory.CreateDbContext();
        return db.ApprovalRequests.AsNoTracking()
            .FirstOrDefault(a => a.Id == requestId && a.TenantId == tenantId);
    }

    /// <summary>Approve a pending request. Fails if it is unknown, already decided, or
    /// the approver is the requester (dynamic SoD: author ≠ approver).</summary>
    public DecideOutcome Approve(string tenantId, string requestId, string approverUserId) =>
        Decide(tenantId, requestId, approverUserId, ApprovalStatus.Approved, enforceDistinct: true);

    /// <summary>Reject a pending request. Any entitled party (including the requester,
    /// cancelling) may reject, so the distinct-party rule does not apply.</summary>
    public DecideOutcome Reject(string tenantId, string requestId, string approverUserId) =>
        Decide(tenantId, requestId, approverUserId, ApprovalStatus.Rejected, enforceDistinct: false);

    private DecideOutcome Decide(
        string tenantId, string requestId, string approverUserId, string status, bool enforceDistinct)
    {
        using var db = _factory.CreateDbContext();
        var request = db.ApprovalRequests.FirstOrDefault(a => a.Id == requestId && a.TenantId == tenantId);
        if (request is null)
        {
            return DecideOutcome.NotFound;
        }
        if (request.Status != ApprovalStatus.Pending)
        {
            return DecideOutcome.NotPending;
        }
        if (enforceDistinct && !SeparationOfDuty.IsDistinctParty(approverUserId, request.RequesterUserId))
        {
            return DecideOutcome.SameParty;
        }
        request.Status = status;
        request.DecidedByUserId = approverUserId;
        request.DecidedAt = _clock.GetUtcNow();
        db.SaveChanges();
        return DecideOutcome.Ok;
    }

    /// <summary>Pending requests in a tenant, oldest first.</summary>
    public IReadOnlyList<ApprovalRequestView> Pending(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        return db.ApprovalRequests.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending)
            .OrderBy(a => a.CreatedAt)
            .AsEnumerable()
            .Select(ToView)
            .ToList();
    }

    private static ApprovalRequestView ToView(ApprovalRequestEntity a) =>
        new(a.Id, a.PermissionKey, a.ScopeId, a.TargetId, a.RequesterUserId, a.Note, a.Status,
            a.CreatedAt, a.DecidedByUserId, a.DecidedAt);
}

/// <summary>An approval request as returned by the admin API.</summary>
public sealed record ApprovalRequestView(
    string Id, string PermissionKey, string? ScopeId, string? TargetId, string RequesterUserId,
    string? Note, string Status, DateTimeOffset CreatedAt, string? DecidedByUserId, DateTimeOffset? DecidedAt);
