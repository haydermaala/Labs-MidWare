// Authorization engine (P2). A single, central decision point:
//   authorize(subject, permission, context) -> allow | deny + reason
// replacing scattered per-endpoint role checks. Deny-by-default: a request is
// allowed only if a role grants the permission AND every step-up requirement
// (MFA, fresh re-auth, approval) on that permission is satisfied. Every decision
// carries a human-readable reason, so denials are explainable ("why can't X?")
// and the old-vs-new decisions can be shadow-logged before enforcement.

namespace ControlPlane.Api;

/// <summary>A single authorization question.</summary>
/// <param name="Roles">The subject's active baseline roles in the relevant scope
/// (a membership carries one today; a collection anticipates P3 multi-role).</param>
/// <param name="PermissionKey">The permission being requested (see <see cref="Permissions"/>).</param>
/// <param name="MfaSatisfied">Whether the session has satisfied MFA.</param>
/// <param name="FreshAuth">Whether the subject re-authenticated recently (step-up).</param>
/// <param name="ApprovalGranted">Whether a required second-party approval is present.</param>
public sealed record AuthorizationRequest(
    IReadOnlyCollection<string> Roles,
    string PermissionKey,
    bool MfaSatisfied = false,
    bool FreshAuth = false,
    bool ApprovalGranted = false);

/// <summary>Allow or deny.</summary>
public enum Decision
{
    Allow,
    Deny,
}

/// <summary>The outcome of an authorization decision, always with a reason.</summary>
public sealed record AuthorizationResult(Decision Decision, string Reason)
{
    /// <summary>Convenience: whether the request was allowed.</summary>
    public bool IsAllowed => Decision == Decision.Allow;

    internal static AuthorizationResult Allow() => new(Decision.Allow, "granted");

    internal static AuthorizationResult Deny(string reason) => new(Decision.Deny, reason);
}

/// <summary>Central authorization decision point.</summary>
public interface IAuthorizationEngine
{
    /// <summary>Decide whether <paramref name="request"/> is permitted, with a reason.</summary>
    AuthorizationResult Authorize(AuthorizationRequest request);
}

/// <summary>
/// Evaluates a request against the permission catalog and role matrix. Stateless
/// and deterministic. In P2 this runs alongside the legacy checks (shadow mode);
/// once parity is confirmed it becomes the sole gate. Scope/attribute predicates
/// and separation-of-duty rules plug in here in P3.
/// </summary>
public sealed class AuthorizationEngine : IAuthorizationEngine
{
    public AuthorizationResult Authorize(AuthorizationRequest request)
    {
        var permission = Permissions.Find(request.PermissionKey);
        if (permission is null)
        {
            // Fail closed: an unknown permission is never granted.
            return AuthorizationResult.Deny($"unknown permission '{request.PermissionKey}'");
        }

        if (request.Roles is null || request.Roles.Count == 0)
        {
            return AuthorizationResult.Deny("subject has no active role in this scope");
        }

        var grantedByAnyRole = request.Roles.Any(role => RolePermissions.Grants(role, permission));
        if (!grantedByAnyRole)
        {
            return AuthorizationResult.Deny($"no role in [{string.Join(", ", request.Roles)}] grants '{permission.Key}'");
        }

        // Role grants the permission — now enforce its step-up requirements.
        if (permission.RequiresMfa && !request.MfaSatisfied)
        {
            return AuthorizationResult.Deny($"'{permission.Key}' requires MFA");
        }

        if (permission.RequiresFreshAuth && !request.FreshAuth)
        {
            return AuthorizationResult.Deny($"'{permission.Key}' requires recent re-authentication (step-up)");
        }

        if (permission.RequiresApproval && !request.ApprovalGranted)
        {
            return AuthorizationResult.Deny($"'{permission.Key}' requires approval by a second party");
        }

        return AuthorizationResult.Allow();
    }
}
