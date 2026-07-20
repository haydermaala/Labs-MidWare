// Tenancy RBAC (LabConnect Phase C3): memberships, baseline roles, invitations.
//
// Every tenant-scoped operation is authorized server-side from the caller's
// membership role — never from anything the client asserts. Roles are a fixed
// baseline set (custom roles are a later feature); capabilities are explicit
// functions so the permission matrix is greppable and testable.

using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace ControlPlane.Api;

/// <summary>The nine baseline roles (PRODUCTION_EXECUTION_PLAN §5.5).</summary>
public static class Roles
{
    public const string Owner = "owner";
    public const string TenantAdmin = "tenant-admin";
    public const string LabAdmin = "lab-admin";
    public const string Technician = "technician";
    public const string MappingReviewer = "mapping-reviewer";
    public const string ClinicalApprover = "clinical-approver";
    public const string BillingAdmin = "billing-admin";
    public const string Auditor = "auditor";
    public const string ReadOnly = "read-only";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Owner, TenantAdmin, LabAdmin, Technician, MappingReviewer,
        ClinicalApprover, BillingAdmin, Auditor, ReadOnly,
    };

    /// <summary>Any active membership may read tenant data (views are further
    /// redacted per role at the payload level as surfaces grow).</summary>
    public static bool CanView(string role) => All.Contains(role);

    /// <summary>Manage the device/gateway fleet: enroll, configure, decommission.</summary>
    public static bool CanManageFleet(string role) =>
        role is Owner or TenantAdmin or LabAdmin;

    /// <summary>Manage people: invite users, revoke invitations, change roles.</summary>
    public static bool CanManageUsers(string role) =>
        role is Owner or TenantAdmin;

    /// <summary>Tenant lifecycle (deactivate/reactivate) stays with owners.</summary>
    public static bool CanManageTenant(string role) => role is Owner;

    /// <summary>Manage billing: view invoices, change plan, open the billing portal.</summary>
    public static bool CanManageBilling(string role) => role is Owner or BillingAdmin;
}

/// <summary>Public view of a membership.</summary>
public sealed record MembershipView(string TenantId, string TenantName, string Role, bool TenantActive);

/// <summary>Public view of a tenant member.</summary>
public sealed record MemberView(string UserId, string Email, string Role, DateTimeOffset Since, bool Active);

/// <summary>Public view of an invitation (never the token; email shown to admins only).</summary>
public sealed record InvitationView(string Id, string Email, string Role, DateTimeOffset ExpiresAt, string Status);

/// <summary>Result of creating an invitation: the one-time link token.</summary>
public sealed record InvitationCreated(InvitationView View, string Token, string TenantName);

/// <summary>Memberships + invitations over the application database.</summary>
public sealed class MembershipService
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IControlPlaneStore _tenants; // tenant truth lives in the fleet store
    private readonly TimeProvider _clock;

    public MembershipService(IDbContextFactory<AppDbContext> factory, IControlPlaneStore tenants, TimeProvider clock)
    {
        _factory = factory;
        _tenants = tenants;
        _clock = clock;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private void Audit(AppDbContext db, string kind, string tenantId, string detail) =>
        db.Audit.Add(new AuditEntity { At = _clock.GetUtcNow(), Kind = kind, TenantId = tenantId, Detail = detail });

    /// <summary>The caller's active role in a tenant, or null (the authorization
    /// primitive for every tenant-scoped endpoint).</summary>
    public string? RoleIn(string userId, string tenantId)
    {
        using var db = _factory.CreateDbContext();
        return db.Memberships.AsNoTracking()
            .FirstOrDefault(m => m.UserId == userId && m.TenantId == tenantId && m.Active)?.Role;
    }

    /// <summary>All active memberships for a user (drives the tenant switcher).</summary>
    public IReadOnlyCollection<MembershipView> MembershipsFor(string userId)
    {
        using var db = _factory.CreateDbContext();
        var tenants = _tenants.Tenants().ToDictionary(t => t.Id);
        return db.Memberships.AsNoTracking().Where(m => m.UserId == userId && m.Active)
            .AsEnumerable()
            .Where(m => tenants.ContainsKey(m.TenantId))
            .Select(m => new MembershipView(m.TenantId, tenants[m.TenantId].Name, m.Role, tenants[m.TenantId].Active))
            .OrderBy(v => v.TenantName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The server-side half of the members query: join + projection only.
    /// Ordering is deliberately NOT applied here — sorting by a property of a
    /// projected record cannot be translated to SQL, and the EF in-memory
    /// provider hides that by evaluating everything client-side. Exposed so a
    /// test can assert this translates on the relational provider.
    /// </summary>
    internal static IQueryable<MemberView> MembersQuery(AppDbContext db, string tenantId) =>
        db.Memberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id,
                (m, u) => new MemberView(u.Id, u.Email, m.Role, m.CreatedAt, m.Active));

    /// <summary>Members of a tenant (admin views), oldest membership first.</summary>
    public IReadOnlyCollection<MemberView> MembersOf(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        return MembersQuery(db, tenantId)
            .AsEnumerable() // order client-side over the materialized rows
            .OrderBy(v => v.Since)
            .ToList();
    }

    /// <summary>Grant a membership directly (bootstrap/platform-admin path).</summary>
    public bool Grant(string userId, string tenantId, string role)
    {
        if (!Roles.All.Contains(role))
        {
            return false;
        }
        using var db = _factory.CreateDbContext();
        if (!_tenants.TenantExists(tenantId) || !db.Users.AsNoTracking().Any(u => u.Id == userId))
        {
            return false;
        }
        var existing = db.Memberships.FirstOrDefault(m => m.UserId == userId && m.TenantId == tenantId);
        if (existing is null)
        {
            db.Memberships.Add(new MembershipEntity
            {
                Id = Ids.New("mem"),
                UserId = userId,
                TenantId = tenantId,
                Role = role,
                CreatedAt = _clock.GetUtcNow(),
                Active = true,
            });
        }
        else
        {
            existing.Role = role;
            existing.Active = true;
        }
        Audit(db, "membership.granted", tenantId, $"{userId} as {role}");
        db.SaveChanges();
        return true;
    }

    /// <summary>Outcome of a membership change (maps to an HTTP status).</summary>
    public enum ChangeResult
    {
        Ok,
        NotFound,
        InvalidRole,
        /// <summary>Refused: a tenant must always keep at least one active owner.</summary>
        LastOwner,
        /// <summary>Refused: only an owner may grant or revoke the owner role.</summary>
        RequiresOwner,
    }

    /// <summary>Whether the tenant would still have an active owner if this
    /// member stopped being one.</summary>
    private static bool WouldStrandTenant(AppDbContext db, string tenantId, MembershipEntity target) =>
        target.Role == Roles.Owner &&
        db.Memberships.Count(m => m.TenantId == tenantId && m.Active && m.Role == Roles.Owner) <= 1;

    /// <summary>
    /// Change a member's role. Beyond the caller's CanManageUsers check, granting
    /// or revoking <c>owner</c> requires the actor to be an owner — otherwise a
    /// tenant-admin could promote themselves — and the last owner cannot be
    /// demoted, which would lock the tenant out of its own administration.
    /// </summary>
    public ChangeResult ChangeRole(string tenantId, string userId, string newRole, string actorRole)
    {
        if (!Roles.All.Contains(newRole))
        {
            return ChangeResult.InvalidRole;
        }
        using var db = _factory.CreateDbContext();
        var membership = db.Memberships.FirstOrDefault(m =>
            m.TenantId == tenantId && m.UserId == userId && m.Active);
        if (membership is null)
        {
            return ChangeResult.NotFound;
        }
        if ((newRole == Roles.Owner || membership.Role == Roles.Owner) && actorRole != Roles.Owner)
        {
            return ChangeResult.RequiresOwner;
        }
        if (membership.Role == newRole)
        {
            return ChangeResult.Ok; // idempotent
        }
        if (newRole != Roles.Owner && WouldStrandTenant(db, tenantId, membership))
        {
            return ChangeResult.LastOwner;
        }
        var previous = membership.Role;
        membership.Role = newRole;
        Audit(db, "membership.role_changed", tenantId, $"{userId}: {previous} -> {newRole}");
        db.SaveChanges();
        return ChangeResult.Ok;
    }

    /// <summary>
    /// Remove a member (soft: the membership is deactivated, so the audit trail
    /// and history remain). Same owner guards as <see cref="ChangeRole"/>.
    /// </summary>
    public ChangeResult RemoveMember(string tenantId, string userId, string actorRole)
    {
        using var db = _factory.CreateDbContext();
        var membership = db.Memberships.FirstOrDefault(m =>
            m.TenantId == tenantId && m.UserId == userId && m.Active);
        if (membership is null)
        {
            return ChangeResult.NotFound;
        }
        if (membership.Role == Roles.Owner && actorRole != Roles.Owner)
        {
            return ChangeResult.RequiresOwner;
        }
        if (WouldStrandTenant(db, tenantId, membership))
        {
            return ChangeResult.LastOwner;
        }
        membership.Active = false;
        Audit(db, "membership.removed", tenantId, $"{userId} ({membership.Role})");
        db.SaveChanges();
        return ChangeResult.Ok;
    }

    /// <summary>Create a single-use, 7-day invitation. Caller must already be
    /// authorized (CanManageUsers) — this method only records + tokens it.</summary>
    public InvitationCreated? Invite(string tenantId, string email, string role, string byUserId)
    {
        if (!Roles.All.Contains(role) || !AuthService.LooksLikeEmail(email))
        {
            return null;
        }
        using var db = _factory.CreateDbContext();
        var tenant = _tenants.Tenants().FirstOrDefault(t => t.Id == tenantId && t.Active);
        if (tenant is null)
        {
            return null;
        }
        var token = "inv_" + Ids.NewSecret();
        var now = _clock.GetUtcNow();
        var invitation = new InvitationEntity
        {
            Id = Ids.New("invt"),
            TenantId = tenantId,
            Email = email.Trim().ToLowerInvariant(),
            Role = role,
            TokenHash = HashToken(token),
            CreatedByUserId = byUserId,
            CreatedAt = now,
            ExpiresAt = now.Add(InvitationLifetime),
        };
        db.Invitations.Add(invitation);
        Audit(db, "invitation.created", tenantId, $"{invitation.Email} as {role}");
        db.SaveChanges();
        return new InvitationCreated(ToView(invitation, now), token, tenant.Name);
    }

    /// <summary>Invitations for a tenant, newest first.</summary>
    public IReadOnlyCollection<InvitationView> InvitationsFor(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        return db.Invitations.AsNoTracking().Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .AsEnumerable()
            .Select(i => ToView(i, now))
            .ToList();
    }

    /// <summary>Revoke a pending invitation (tenant-scoped).</summary>
    public bool RevokeInvitation(string tenantId, string invitationId)
    {
        using var db = _factory.CreateDbContext();
        var invitation = db.Invitations.FirstOrDefault(i =>
            i.Id == invitationId && i.TenantId == tenantId && i.AcceptedAt == null && i.RevokedAt == null);
        if (invitation is null)
        {
            return false;
        }
        invitation.RevokedAt = _clock.GetUtcNow();
        Audit(db, "invitation.revoked", tenantId, invitation.Email);
        db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Accept an invitation as the authenticated user. The invitation email must
    /// match the user's email (invitations are not bearer-transferable), and the
    /// token is single-use and time-bounded.
    /// </summary>
    public MembershipView? Accept(string token, string userId)
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        var invitation = db.Invitations.FirstOrDefault(i =>
            i.TokenHash == HashToken(token) && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now);
        if (invitation is null)
        {
            return null;
        }
        var user = db.Users.AsNoTracking().FirstOrDefault(u => u.Id == userId && u.Active);
        var tenant = _tenants.Tenants().FirstOrDefault(t => t.Id == invitation.TenantId && t.Active);
        if (user is null || tenant is null || !string.Equals(user.Email, invitation.Email, StringComparison.Ordinal))
        {
            return null;
        }
        invitation.AcceptedAt = now;
        var existing = db.Memberships.FirstOrDefault(m => m.UserId == userId && m.TenantId == invitation.TenantId);
        if (existing is null)
        {
            db.Memberships.Add(new MembershipEntity
            {
                Id = Ids.New("mem"),
                UserId = userId,
                TenantId = invitation.TenantId,
                Role = invitation.Role,
                CreatedAt = now,
                Active = true,
            });
        }
        else
        {
            existing.Role = invitation.Role;
            existing.Active = true;
        }
        Audit(db, "invitation.accepted", invitation.TenantId, $"{invitation.Email} as {invitation.Role}");
        db.SaveChanges();
        return new MembershipView(tenant.Id, tenant.Name, invitation.Role, tenant.Active);
    }

    private static InvitationView ToView(InvitationEntity i, DateTimeOffset now) =>
        new(i.Id, i.Email, i.Role, i.ExpiresAt,
            i.RevokedAt is not null ? "revoked"
            : i.AcceptedAt is not null ? "accepted"
            : i.ExpiresAt <= now ? "expired"
            : "pending");
}
