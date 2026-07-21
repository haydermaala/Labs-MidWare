// Scoped role assignments (P3, ADR 0020 §2). A subject holds a role at a scope,
// optionally with an expiry. Unlike the flat `memberships.role` (one role per
// tenant), a subject may hold several assignments at different scopes, and the
// effective grant at a resource is the union over every assignment whose scope
// *contains* that resource's scope (ScopeTree.Contains) and has not expired.
//
// This is the model + resolution logic. Persisting assignments (the entity below),
// migrating memberships into tenant-root assignments, and making the
// AuthorizationEngine consume this are the next P3 slices.

namespace ControlPlane.Api;

/// <summary>A subject's grant of a role at a scope, optionally time-bounded.</summary>
public sealed record RoleAssignment(
    string Id,
    string UserId,
    string Role,
    string ScopeId,
    DateTimeOffset? ExpiresAt);

/// <summary>Resolution over a set of scoped role assignments.</summary>
public static class RoleAssignments
{
    /// <summary>Whether an assignment is in force at <paramref name="now"/>
    /// (no expiry, or not yet expired).</summary>
    public static bool IsActiveAt(RoleAssignment assignment, DateTimeOffset now) =>
        assignment.ExpiresAt is null || assignment.ExpiresAt.Value > now;

    /// <summary>
    /// The roles a subject effectively holds at <paramref name="targetScopeId"/>:
    /// the role of every one of the subject's assignments that is still active and
    /// whose scope contains the target (i.e. is the target or an ancestor of it).
    /// An assignment at the tenant root therefore applies everywhere — matching
    /// today's single tenant-wide membership role.
    /// </summary>
    public static IReadOnlySet<string> EffectiveRolesAt(
        IEnumerable<RoleAssignment> assignments,
        ScopeTree tree,
        string userId,
        string targetScopeId,
        DateTimeOffset now) =>
        assignments
            .Where(a =>
                string.Equals(a.UserId, userId, StringComparison.Ordinal) &&
                IsActiveAt(a, now) &&
                tree.Contains(a.ScopeId, targetScopeId))
            .Select(a => a.Role)
            .ToHashSet(StringComparer.Ordinal);
}
