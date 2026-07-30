// Separation of duty (P3, ADR 0020 §5). Two flavours:
//   • Static — a single subject may not simultaneously hold both permissions of a
//     rule (e.g. the person who authors a mapping must not also be able to approve
//     it). Enforced when granting a role/assignment and auditable at review time.
//   • Dynamic — even when two parties each hold the needed permission, the same
//     person may not fill both roles in one transaction (author ≠ approver,
//     requester ≠ approver). Enforced at the approval step.
//
// This is the rule model + checks. The rules table (SodRuleEntity) is seeded per
// tenant; wiring the checks into the grant path and the approval flow are later
// slices. The P2 `RequiresApproval` permission flag marks which actions the
// dynamic check gates.

namespace ControlPlane.Api;

/// <summary>A static separation-of-duty rule: no single subject may hold both
/// permissions at once. Order of the two sides is irrelevant.</summary>
public sealed record SodRule(string Id, string Name, string PermissionA, string PermissionB)
{
    /// <summary>Whether this rule concerns <paramref name="permissionKey"/>, and if
    /// so, the other permission it conflicts with.</summary>
    public string? Counterpart(string permissionKey) =>
        string.Equals(permissionKey, PermissionA, StringComparison.Ordinal) ? PermissionB
        : string.Equals(permissionKey, PermissionB, StringComparison.Ordinal) ? PermissionA
        : null;
}

/// <summary>Separation-of-duty evaluation.</summary>
public static class SeparationOfDuty
{
    /// <summary>The rules a set of held permissions currently violates (both sides
    /// held) — for access reviews.</summary>
    public static IReadOnlyCollection<SodRule> StaticViolations(
        IReadOnlySet<string> heldPermissionKeys, IEnumerable<SodRule> rules) =>
        rules
            .Where(r => heldPermissionKeys.Contains(r.PermissionA) && heldPermissionKeys.Contains(r.PermissionB))
            .ToList();

    /// <summary>Whether adding <paramref name="candidatePermissionKey"/> to a subject
    /// that already holds <paramref name="held"/> would breach any rule — the gate a
    /// grant must pass.</summary>
    public static bool WouldViolate(
        IReadOnlySet<string> held, string candidatePermissionKey, IEnumerable<SodRule> rules) =>
        rules.Any(r => r.Counterpart(candidatePermissionKey) is { } other && held.Contains(other));

    /// <summary>Dynamic SoD: the approver of an action must be a different person
    /// than the requester/author.</summary>
    public static bool IsDistinctParty(string actorUserId, string subjectUserId) =>
        !string.Equals(actorUserId, subjectUserId, StringComparison.Ordinal);
}
