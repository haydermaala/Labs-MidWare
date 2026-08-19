// Static separation of duty for PLATFORM roles.
//
// The tenant-side model (SeparationOfDuty.cs) has both flavours: static — one subject may
// not HOLD both sides of a conflicting pair — and dynamic — even when two people each hold
// a side, the same person may not fill both in one transaction.
//
// The platform side had only the dynamic half. PlatformSupportService refuses to let the
// requester approve their own grant, but nothing stopped one human from being granted both
// platform-support-engineer and platform-security-admin. Dynamic SoD then reduces to a
// formality: the same person requests under one hat and approves under the other, needing
// only two sessions, and every audit row looks correct because two distinct role
// assignments really were involved.
//
// This closes that at the grant path, which is the only place a platform role is conferred.

namespace ControlPlane.Api;

/// <summary>Platform-wide static separation-of-duty rules.</summary>
public static class PlatformSeparationOfDuty
{
    /// <summary>
    /// Conflicting permission pairs. One entry today: support-grant request and approval.
    ///
    /// Tenant offboarding is deliberately NOT here — both its request and its approval are
    /// gated by the same permission (PlatformPermissions.TenantOffboard), so there is no
    /// pair to separate statically and its protection is dynamic SoD alone
    /// (PlatformOffboardService's distinct-party check).
    /// </summary>
    public static readonly IReadOnlyList<SodRule> Rules =
    [
        new SodRule(
            "psod_support_request_approve",
            "support-grant requester must not also be an approver",
            PlatformPermissions.SupportRequest.Key,
            PlatformPermissions.SupportApprove.Key),
    ];

    /// <summary>The permissions a set of platform roles confers.</summary>
    public static IReadOnlySet<string> PermissionsOf(IEnumerable<string> roles)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            all.UnionWith(PlatformRolePermissions.PermissionsOf(role));
        }
        return all;
    }

    /// <summary>
    /// The rules that granting <paramref name="candidateRole"/> would NEWLY breach for a
    /// user already holding <paramref name="heldRoles"/>. Empty means the grant is allowed.
    ///
    /// Only NEW breaches count. A pre-existing violation — from Root Owner, or from a rule
    /// added after the fact — must not block an unrelated grant, or adding a rule would
    /// freeze the affected accounts out of every future role change.
    ///
    /// Root Owner is exempt as the grantable role: it holds every permission, so by
    /// construction it holds both sides of every pair. That is what break-glass means, it
    /// is why the role is Critical and MFA-gated, and dynamic SoD still stops a Root Owner
    /// approving their own request.
    /// </summary>
    public static IReadOnlyCollection<SodRule> WouldNewlyViolate(
        IEnumerable<string> heldRoles, string candidateRole)
    {
        if (string.Equals(candidateRole, PlatformRoles.RootOwner, StringComparison.Ordinal))
        {
            return [];
        }

        var held = PermissionsOf(heldRoles);
        var resulting = new HashSet<string>(held, StringComparer.Ordinal);
        resulting.UnionWith(PlatformRolePermissions.PermissionsOf(candidateRole));

        var before = SeparationOfDuty.StaticViolations(held, Rules).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return SeparationOfDuty.StaticViolations(resulting, Rules)
            .Where(r => !before.Contains(r.Id))
            .ToList();
    }
}
