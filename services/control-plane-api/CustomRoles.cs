// Custom roles + delegation (P3, ADR 0020 §3). A tenant may define its own roles
// as a named set of permission keys (persisted in custom_roles + role_permissions),
// alongside the nine code-owned baseline roles. RoleGrants unifies "does this role
// grant this permission?" across both. Delegation limits bound what a grantor may
// put into a custom role or otherwise grant: only permissions the grantor holds
// and that the catalog marks Delegable (and, enforced separately by the scope
// engine, only at or below the grantor's own scope).
//
// This is the model + rules. The admin API to create custom roles and the
// entitlement gate on the feature are later slices.

namespace ControlPlane.Api;

/// <summary>Resolves permission grants for baseline and custom roles.</summary>
public static class RoleGrants
{
    /// <summary>The permission keys a baseline role grants — an explicit view of the
    /// P2 role matrix, and the basis for delegation checks.</summary>
    public static IReadOnlySet<string> BaselinePermissions(string role) =>
        Permissions.All
            .Where(p => RolePermissions.Grants(role, p))
            .Select(p => p.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="role"/> grants <paramref name="permissionKey"/>.
    /// Baseline roles use the code matrix; any other role is treated as custom and
    /// resolved from <paramref name="customGrants"/> (roleKey → permission keys).</summary>
    public static bool Grants(
        string role, string permissionKey, IReadOnlyDictionary<string, IReadOnlySet<string>> customGrants)
    {
        if (Roles.All.Contains(role))
        {
            var permission = Permissions.Find(permissionKey);
            return permission is not null && RolePermissions.Grants(role, permission);
        }
        return customGrants.TryGetValue(role, out var keys) && keys.Contains(permissionKey);
    }
}

/// <summary>Delegation limits on what a grantor may grant / put into a custom role.</summary>
public static class Delegation
{
    /// <summary>Whether a grantor holding <paramref name="grantorPermissionKeys"/> may
    /// delegate <paramref name="permissionKey"/>: it must be a known, delegable
    /// permission that the grantor themselves holds.</summary>
    public static bool CanDelegate(IReadOnlySet<string> grantorPermissionKeys, string permissionKey)
    {
        var permission = Permissions.Find(permissionKey);
        return permission is not null && permission.Delegable && grantorPermissionKeys.Contains(permissionKey);
    }

    /// <summary>Restrict a desired permission set to those the grantor may delegate —
    /// the enforceable ceiling on a custom role a grantor is defining.</summary>
    public static IReadOnlySet<string> Allowed(IReadOnlySet<string> grantorPermissionKeys, IEnumerable<string> desired) =>
        desired.Where(k => CanDelegate(grantorPermissionKeys, k)).ToHashSet(StringComparer.Ordinal);
}
