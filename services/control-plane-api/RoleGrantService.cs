// Scoped role grants + custom roles admin service (P3, ADR 0020 §2/§3/§5).
//
// This is the write-side that finally makes the P3 model reachable: a tenant
// admin grants a role to a user at a scope (a row in role_assignments), revokes
// it, and defines custom roles (custom_roles + role_permissions). Two invariants
// are enforced on every grant, straight from the model:
//   • Delegation — a grantor may only hand out permissions that are Delegable and
//     that the grantor themselves holds (Delegation.CanDelegate). So a scoped
//     grant can never escalate beyond the grantor, and a non-delegable permission
//     (e.g. tenant.deactivate) can never be spread this way.
//   • Separation of duty — the grant must not leave the target holding both sides
//     of any active SoD rule (SeparationOfDuty.StaticViolations over the resulting
//     permission set).
//
// Like ScopeService/MembershipService this talks to EF directly and filters by
// tenantId in the app layer. RLS policies for these tables land when P1 and P3
// merge (see the program plan); TenantScope is not on this branch.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Grants/revokes scoped role assignments and defines custom roles,
/// enforcing delegation limits and separation-of-duty.</summary>
public sealed class RoleGrantService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public RoleGrantService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public enum GrantOutcome { Ok, UnknownScope, UnknownRole, DelegationDenied, SodViolation }

    /// <summary>Result of a grant. <see cref="Offending"/> carries the blocking
    /// permission keys (delegation) or rule names (SoD) for a helpful API error.</summary>
    public sealed record GrantResult(
        GrantOutcome Outcome, RoleAssignmentView? Assignment, IReadOnlyList<string> Offending);

    /// <summary>Grant <paramref name="role"/> to <paramref name="targetUserId"/> at
    /// <paramref name="scopeId"/>. <paramref name="grantorRole"/> is the granting
    /// actor's effective role (its permissions are the delegation ceiling).</summary>
    public GrantResult Grant(
        string tenantId, string grantorUserId, string grantorRole,
        string targetUserId, string role, string scopeId, DateTimeOffset? expiresAt)
    {
        using var db = _factory.CreateDbContext();

        if (!db.Scopes.Any(s => s.Id == scopeId && s.TenantId == tenantId))
        {
            return new GrantResult(GrantOutcome.UnknownScope, null, []);
        }
        if (!IsKnownRole(db, tenantId, role))
        {
            return new GrantResult(GrantOutcome.UnknownRole, null, []);
        }

        var grantorPermissions = PermissionsOfRole(db, tenantId, grantorRole);
        var grantedPermissions = PermissionsOfRole(db, tenantId, role);

        var notDelegable = grantedPermissions
            .Where(k => !Delegation.CanDelegate(grantorPermissions, k))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (notDelegable.Count > 0)
        {
            return new GrantResult(GrantOutcome.DelegationDenied, null, notDelegable);
        }

        var rules = ActiveRules(db, tenantId);
        var resulting = HeldPermissions(db, tenantId, targetUserId);
        resulting.UnionWith(grantedPermissions);
        var violated = SeparationOfDuty.StaticViolations(resulting, rules);
        if (violated.Count > 0)
        {
            return new GrantResult(
                GrantOutcome.SodViolation, null, violated.Select(r => r.Name).ToList());
        }

        var entity = new RoleAssignmentEntity
        {
            Id = Ids.New("rga"),
            TenantId = tenantId,
            UserId = targetUserId,
            Role = role,
            ScopeId = scopeId,
            GrantedByUserId = grantorUserId,
            CreatedAt = _clock.GetUtcNow(),
            ExpiresAt = expiresAt,
        };
        db.RoleAssignments.Add(entity);
        db.SaveChanges();
        return new GrantResult(GrantOutcome.Ok, ToView(entity), []);
    }

    /// <summary>Soft-revoke an assignment. Returns false if it is unknown in the
    /// tenant or was already revoked.</summary>
    public bool Revoke(string tenantId, string assignmentId)
    {
        using var db = _factory.CreateDbContext();
        var a = db.RoleAssignments.FirstOrDefault(x => x.Id == assignmentId && x.TenantId == tenantId);
        if (a is null || a.RevokedAt is not null)
        {
            return false;
        }
        a.RevokedAt = _clock.GetUtcNow();
        db.SaveChanges();
        return true;
    }

    /// <summary>The subject's non-revoked assignments in the tenant as model records,
    /// for the scope-aware engine (which further ignores any that are expired).</summary>
    public IReadOnlyList<RoleAssignment> ActiveAssignmentsFor(string tenantId, string userId)
    {
        using var db = _factory.CreateDbContext();
        return db.RoleAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.UserId == userId && a.RevokedAt == null)
            .AsEnumerable()
            .Select(a => new RoleAssignment(a.Id, a.UserId, a.Role, a.ScopeId, a.ExpiresAt))
            .ToList();
    }

    /// <summary>A tenant's role assignments, newest last; optionally for one user.</summary>
    public IReadOnlyList<RoleAssignmentView> AssignmentsFor(string tenantId, string? userId)
    {
        using var db = _factory.CreateDbContext();
        var rows = db.RoleAssignments.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (userId is not null)
        {
            rows = rows.Where(a => a.UserId == userId);
        }
        return rows.OrderBy(a => a.CreatedAt).AsEnumerable().Select(ToView).ToList();
    }

    public enum CustomRoleOutcome { Ok, ReservedRoleKey, RoleKeyTaken, NoValidPermissions, DelegationDenied }

    public sealed record CustomRoleResult(
        CustomRoleOutcome Outcome, CustomRoleView? Role, IReadOnlyList<string> Offending);

    /// <summary>Define a custom role from <paramref name="permissionKeys"/>. The
    /// creator may only include permissions they can delegate; unknown keys are
    /// dropped, and a key the creator cannot delegate blocks the whole create.</summary>
    public CustomRoleResult CreateCustomRole(
        string tenantId, string creatorUserId, string creatorRole,
        string roleKey, string name, IReadOnlyList<string> permissionKeys)
    {
        using var db = _factory.CreateDbContext();

        if (Roles.All.Contains(roleKey))
        {
            return new CustomRoleResult(CustomRoleOutcome.ReservedRoleKey, null, []);
        }
        if (db.CustomRoles.Any(r => r.TenantId == tenantId && r.RoleKey == roleKey))
        {
            return new CustomRoleResult(CustomRoleOutcome.RoleKeyTaken, null, []);
        }

        var desired = permissionKeys
            .Where(k => Permissions.Find(k) is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (desired.Count == 0)
        {
            return new CustomRoleResult(CustomRoleOutcome.NoValidPermissions, null, []);
        }

        var creatorPermissions = PermissionsOfRole(db, tenantId, creatorRole);
        var notDelegable = desired
            .Where(k => !Delegation.CanDelegate(creatorPermissions, k))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (notDelegable.Count > 0)
        {
            return new CustomRoleResult(CustomRoleOutcome.DelegationDenied, null, notDelegable);
        }

        var entity = new CustomRoleEntity
        {
            Id = Ids.New("crole"),
            TenantId = tenantId,
            RoleKey = roleKey,
            Name = name,
            CreatedByUserId = creatorUserId,
            CreatedAt = _clock.GetUtcNow(),
        };
        db.CustomRoles.Add(entity);
        foreach (var key in desired.Order(StringComparer.Ordinal))
        {
            db.RolePermissions.Add(new RolePermissionEntity
            {
                Id = Ids.New("rperm"),
                TenantId = tenantId,
                RoleKey = roleKey,
                PermissionKey = key,
            });
        }
        db.SaveChanges();
        return new CustomRoleResult(
            CustomRoleOutcome.Ok, new CustomRoleView(entity.RoleKey, entity.Name, [.. desired.Order(StringComparer.Ordinal)]), []);
    }

    /// <summary>A tenant's custom roles with their permission keys.</summary>
    public IReadOnlyList<CustomRoleView> CustomRolesFor(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        var perms = db.RolePermissions.AsNoTracking()
            .Where(rp => rp.TenantId == tenantId)
            .AsEnumerable()
            .GroupBy(rp => rp.RoleKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(rp => rp.PermissionKey).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        return db.CustomRoles.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.CreatedAt)
            .AsEnumerable()
            .Select(r => new CustomRoleView(
                r.RoleKey, r.Name,
                perms.TryGetValue(r.RoleKey, out var keys) ? keys : []))
            .ToList();
    }

    private static bool IsKnownRole(AppDbContext db, string tenantId, string role) =>
        Roles.All.Contains(role) || db.CustomRoles.Any(r => r.TenantId == tenantId && r.RoleKey == role);

    // The permission keys a role confers: baseline roles from the code matrix,
    // custom roles from their role_permissions rows.
    private static HashSet<string> PermissionsOfRole(AppDbContext db, string tenantId, string role)
    {
        if (Roles.All.Contains(role))
        {
            return RoleGrants.BaselinePermissions(role).ToHashSet(StringComparer.Ordinal);
        }
        return db.RolePermissions
            .Where(rp => rp.TenantId == tenantId && rp.RoleKey == role)
            .Select(rp => rp.PermissionKey)
            .AsEnumerable()
            .ToHashSet(StringComparer.Ordinal);
    }

    // Everything the target already holds in the tenant: their active (unrevoked,
    // unexpired) role assignments plus their baseline membership role.
    private HashSet<string> HeldPermissions(AppDbContext db, string tenantId, string userId)
    {
        var now = _clock.GetUtcNow();
        var held = new HashSet<string>(StringComparer.Ordinal);

        var assignmentRoles = db.RoleAssignments
            .Where(a => a.TenantId == tenantId && a.UserId == userId && a.RevokedAt == null
                && (a.ExpiresAt == null || a.ExpiresAt > now))
            .Select(a => a.Role)
            .AsEnumerable()
            .Distinct(StringComparer.Ordinal);
        foreach (var role in assignmentRoles)
        {
            held.UnionWith(PermissionsOfRole(db, tenantId, role));
        }

        var membershipRole = db.Memberships
            .Where(m => m.TenantId == tenantId && m.UserId == userId && m.Active)
            .Select(m => m.Role)
            .FirstOrDefault();
        if (membershipRole is not null)
        {
            held.UnionWith(PermissionsOfRole(db, tenantId, membershipRole));
        }
        return held;
    }

    private static List<SodRule> ActiveRules(AppDbContext db, string tenantId) =>
        db.SodRules
            .Where(r => r.TenantId == tenantId && r.Active)
            .AsEnumerable()
            .Select(r => new SodRule(r.Id, r.Name, r.PermissionA, r.PermissionB))
            .ToList();

    private static RoleAssignmentView ToView(RoleAssignmentEntity a) =>
        new(a.Id, a.UserId, a.Role, a.ScopeId, a.GrantedByUserId, a.CreatedAt, a.ExpiresAt,
            a.RevokedAt is null && (a.ExpiresAt is null || a.ExpiresAt > DateTimeOffset.UtcNow));
}

/// <summary>A role assignment as returned by the admin API.</summary>
public sealed record RoleAssignmentView(
    string Id, string UserId, string Role, string ScopeId, string GrantedByUserId,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool Active);

/// <summary>A custom role with its permission keys.</summary>
public sealed record CustomRoleView(string RoleKey, string Name, IReadOnlyList<string> PermissionKeys);
