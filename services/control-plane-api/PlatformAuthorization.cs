// Platform authorization decision point (P6, program prompt §8). Mirrors the tenant
// AuthorizationEngine (deny-by-default, step-up gates) but over the platform catalog
// and role matrix. Kept SEPARATE from the tenant engine on purpose: platform access
// is never inferred from tenant roles, and a platform decision never consults tenant
// membership. Reuses the shared AuthorizationResult (allow | deny + reason, with a
// RequiresStepUp flag the UI can act on).

namespace ControlPlane.Api;

/// <summary>A platform authorization question.</summary>
/// <param name="Roles">The subject's active PLATFORM role assignments (never tenant
/// roles).</param>
/// <param name="PermissionKey">The platform permission being requested.</param>
/// <param name="MfaSatisfied">Whether the session has satisfied MFA.</param>
/// <param name="FreshAuth">Whether the subject re-authenticated recently (step-up).</param>
public sealed record PlatformAuthorizationRequest(
    IReadOnlyCollection<string> Roles,
    string PermissionKey,
    bool MfaSatisfied = false,
    bool FreshAuth = false);

/// <summary>Platform decision point.</summary>
public interface IPlatformAuthorizationEngine
{
    AuthorizationResult Authorize(PlatformAuthorizationRequest request);
}

/// <summary>
/// Evaluates a platform request against the platform catalog + role matrix.
/// Deny-by-default. A permission's own MFA/fresh-auth requirements are enforced, and
/// the break-glass Root Owner role always demands MFA + fresh auth — so it can never
/// be used for low-friction routine work.
/// </summary>
public sealed class PlatformAuthorizationEngine : IPlatformAuthorizationEngine
{
    public AuthorizationResult Authorize(PlatformAuthorizationRequest request)
    {
        var permission = PlatformPermissions.Find(request.PermissionKey);
        if (permission is null)
        {
            return AuthorizationResult.Deny($"unknown platform permission '{request.PermissionKey}'");
        }
        if (request.Roles is null || request.Roles.Count == 0)
        {
            return AuthorizationResult.Deny("subject holds no platform role");
        }

        var grantedByAnyRole = request.Roles.Any(r => PlatformRolePermissions.Grants(r, permission.Key));
        if (!grantedByAnyRole)
        {
            return AuthorizationResult.Deny(
                $"no platform role in [{string.Join(", ", request.Roles)}] grants '{permission.Key}'");
        }

        // The permission's own step-up requirements.
        if (permission.RequiresMfa && !request.MfaSatisfied)
        {
            return AuthorizationResult.DenyStepUp($"'{permission.Key}' requires MFA");
        }
        if (permission.RequiresFreshAuth && !request.FreshAuth)
        {
            return AuthorizationResult.DenyStepUp($"'{permission.Key}' requires recent re-authentication (step-up)");
        }

        // Break-glass elevation: if the grant relies on the Root Owner role (no other
        // held role grants it), demand MFA + fresh auth regardless of the permission's
        // own metadata — Root is never for low-assurance routine work.
        var grantedByNonBreakGlass = request.Roles.Any(r =>
            !PlatformRoles.IsBreakGlass(r) && PlatformRolePermissions.Grants(r, permission.Key));
        if (!grantedByNonBreakGlass)
        {
            if (!request.MfaSatisfied)
            {
                return AuthorizationResult.DenyStepUp("break-glass (Root Owner) requires MFA");
            }
            if (!request.FreshAuth)
            {
                return AuthorizationResult.DenyStepUp("break-glass (Root Owner) requires recent re-authentication");
            }
        }

        return AuthorizationResult.Allow();
    }
}
