// Scope-aware authorization (P3, ADR 0020 §4). Extends the P2 permission engine
// with the org hierarchy: a request now targets a *scope*, and the subject's
// effective roles there are the union of their assignments whose scope contains
// the target (RoleAssignments.EffectiveRolesAt). Those roles then flow through the
// same deny-by-default permission + step-up checks as P2 — so a tenant with only
// the root scope, or a subject with a single tenant-root assignment, behaves
// exactly as before.

namespace ControlPlane.Api;

/// <summary>An authorization question against a specific scope.</summary>
/// <param name="Assignments">The subject's scoped role assignments (already
/// filtered to the tenant; expired/revoked rows may be included — they are
/// ignored by the resolver).</param>
/// <param name="Tree">The tenant's validated scope tree.</param>
/// <param name="UserId">The subject.</param>
/// <param name="TargetScopeId">The scope the action targets (the resource's scope).</param>
/// <param name="PermissionKey">The permission being requested.</param>
/// <param name="Now">Evaluation time, for assignment expiry.</param>
public sealed record ScopedAuthorizationRequest(
    IReadOnlyCollection<RoleAssignment> Assignments,
    ScopeTree Tree,
    string UserId,
    string TargetScopeId,
    string PermissionKey,
    DateTimeOffset Now,
    bool MfaSatisfied = false,
    bool FreshAuth = false,
    bool ApprovalGranted = false);

/// <summary>Scope-aware decision point layered over <see cref="IAuthorizationEngine"/>.</summary>
public interface IScopedAuthorizationEngine
{
    AuthorizationResult Authorize(ScopedAuthorizationRequest request);
}

/// <summary>
/// Resolves the subject's effective roles at the target scope, then delegates the
/// permission + step-up decision to the underlying (P2) engine. Deny-by-default:
/// if the target scope is unknown or the subject holds no active role that reaches
/// it, the request is denied with a reason before any permission is considered.
/// </summary>
public sealed class ScopedAuthorizationEngine(IAuthorizationEngine inner) : IScopedAuthorizationEngine
{
    public AuthorizationResult Authorize(ScopedAuthorizationRequest request)
    {
        if (request.Tree.Find(request.TargetScopeId) is null)
        {
            return AuthorizationResult.Deny($"unknown scope '{request.TargetScopeId}'");
        }

        var roles = RoleAssignments.EffectiveRolesAt(
            request.Assignments, request.Tree, request.UserId, request.TargetScopeId, request.Now);
        if (roles.Count == 0)
        {
            return AuthorizationResult.Deny($"no active role at scope '{request.TargetScopeId}'");
        }

        return inner.Authorize(new AuthorizationRequest(
            roles.ToList(),
            request.PermissionKey,
            request.MfaSatisfied,
            request.FreshAuth,
            request.ApprovalGranted));
    }
}
