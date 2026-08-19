// Platform (super-admin) role-assignment persistence (P6, program prompt §5.1).
// Records which GLOBAL users hold which platform roles, and resolves a user's active
// platform roles for the PlatformAuthorizationEngine — the disjoint counterpart to
// MembershipService/RoleGrantService on the tenant side.
//
// GLOBAL, not tenant-scoped: platform_role_assignments carries no TenantId and is not
// under Row-Level Security (like users/sessions), so there is no TenantScope here.
// A platform role assignment is never inferred from a tenant membership.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Grants/revokes platform role assignments and resolves a user's active
/// platform roles.</summary>
public sealed class PlatformAdminService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public PlatformAdminService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public enum GrantOutcome { Ok, UnknownRole, SodViolation }

    public sealed record GrantResult(
        GrantOutcome Outcome,
        PlatformRoleAssignmentView? Assignment,
        IReadOnlyCollection<string>? ViolatedRules = null);

    /// <summary>Assign a platform role to a user. <paramref name="reason"/> should be
    /// present for break-glass (Root Owner) grants. Fails if the role is unknown.</summary>
    public GrantResult Grant(
        string granterUserId, string targetUserId, string role,
        DateTimeOffset? expiresAt, string? reason)
    {
        if (!PlatformRoles.All.Contains(role))
        {
            return new GrantResult(GrantOutcome.UnknownRole, null);
        }

        // Static separation of duty. Without this the platform had only the dynamic half:
        // PlatformSupportService refuses self-approval, but one human granted both
        // support-engineer and security-admin could request under one hat and approve
        // under the other, and every audit row would look correct because two genuinely
        // distinct role assignments were involved. The grant path is the only place a
        // platform role is conferred, so it is the only place this has to hold.
        var violations = PlatformSeparationOfDuty.WouldNewlyViolate(RolesFor(targetUserId), role);
        if (violations.Count > 0)
        {
            return new GrantResult(GrantOutcome.SodViolation, null,
                violations.Select(r => r.Name).ToList());
        }

        using var db = _factory.CreateDbContext();
        var entity = new PlatformRoleAssignmentEntity
        {
            Id = Ids.New("pra"),
            UserId = targetUserId,
            Role = role,
            GrantedByUserId = granterUserId,
            Reason = reason,
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = expiresAt,
        };
        db.PlatformRoleAssignments.Add(entity);
        db.SaveChanges();
        return new GrantResult(GrantOutcome.Ok, ToView(entity, _clock.GetUtcNow()));
    }

    /// <summary>Soft-revoke a platform role assignment. Returns false if unknown or
    /// already revoked.</summary>
    public bool Revoke(string assignmentId)
    {
        using var db = _factory.CreateDbContext();
        var a = db.PlatformRoleAssignments.FirstOrDefault(x => x.Id == assignmentId);
        if (a is null || a.RevokedAt is not null)
        {
            return false;
        }
        a.RevokedAt = _clock.GetUtcNow();
        db.SaveChanges();
        return true;
    }

    /// <summary>The platform roles a user effectively holds now: active (unrevoked,
    /// unexpired) assignments. Empty for a user with no platform access — the common
    /// case, since platform roles are held by very few people.</summary>
    public IReadOnlySet<string> RolesFor(string userId)
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        return db.PlatformRoleAssignments.AsNoTracking()
            .Where(a => a.UserId == userId && a.RevokedAt == null && (a.ExpiresAt == null || a.ExpiresAt > now))
            .Select(a => a.Role)
            .AsEnumerable()
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Platform role assignments, newest last; optionally for one user.</summary>
    public IReadOnlyList<PlatformRoleAssignmentView> Assignments(string? userId)
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        var rows = db.PlatformRoleAssignments.AsNoTracking();
        if (userId is not null)
        {
            rows = rows.Where(a => a.UserId == userId);
        }
        return rows.OrderBy(a => a.CreatedAt).AsEnumerable().Select(a => ToView(a, now)).ToList();
    }

    private static PlatformRoleAssignmentView ToView(PlatformRoleAssignmentEntity a, DateTimeOffset now) =>
        new(a.Id, a.UserId, a.Role, a.GrantedByUserId, a.Reason, a.CreatedAt, a.ExpiresAt,
            a.RevokedAt is null && (a.ExpiresAt is null || a.ExpiresAt > now));
}

/// <summary>A platform role assignment as returned by the admin API.</summary>
public sealed record PlatformRoleAssignmentView(
    string Id, string UserId, string Role, string GrantedByUserId, string? Reason,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Active);
