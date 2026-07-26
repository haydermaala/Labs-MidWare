// Tenant lifecycle state machine (prompt §10, §13.1). A tenant is not just
// active/inactive: it moves through provisioning, trial, active, a past-due grace
// period, suspension, an offboarding pipeline, and finally an archived terminal
// state. Governance decisions (may this tenant enrol devices? can it be restored?
// can it be archived yet?) hang off the state, so the transitions are guarded and
// deny-by-default rather than free boolean flips.
//
// This is the pure domain layer — the enum, the transition graph, the operation
// intents, and the mapping from the legacy (Active, Offboarded) booleans. Persisting
// the status column, wiring the platform lifecycle endpoints onto it, and the
// cooling-off / retention / legal-hold windows are the following slices.

namespace ControlPlane.Api;

/// <summary>The lifecycle state of a tenant, in rough forward order. The order of
/// declaration is not itself the transition rule — <see cref="TenantLifecycle"/>
/// owns the allowed edges.</summary>
public enum TenantStatus
{
    /// <summary>Being created; not yet usable.</summary>
    Provisioning,

    /// <summary>Usable, on a time- or feature-limited trial.</summary>
    Trial,

    /// <summary>Fully active, paid.</summary>
    Active,

    /// <summary>Past-due / payment issue but still operating under a grace window.</summary>
    Grace,

    /// <summary>Administratively suspended; operations halted but recoverable.</summary>
    Suspended,

    /// <summary>Offboarding pipeline running (export → retention/legal-hold → deletion);
    /// still cancellable back to a live state during the cooling-off window.</summary>
    Offboarding,

    /// <summary>Terminal: offboarding completed, data deleted/anonymized. Not reversible.</summary>
    Archived,
}

/// <summary>A named lifecycle operation an operator requests, distinct from the raw
/// (from, to) edge so callers speak intent and the mapping is auditable.</summary>
public enum TenantLifecycleOperation
{
    /// <summary>Bring a tenant live (from provisioning/trial/grace/suspended → active).</summary>
    Activate,

    /// <summary>Move an active-ish tenant into the past-due grace window.</summary>
    EnterGrace,

    /// <summary>Administratively suspend.</summary>
    Suspend,

    /// <summary>Restore a suspended tenant to active.</summary>
    Restore,

    /// <summary>Begin the offboarding pipeline.</summary>
    BeginOffboarding,

    /// <summary>Cancel offboarding during cooling-off, returning to active.</summary>
    CancelOffboarding,

    /// <summary>Complete offboarding into the terminal archived state.</summary>
    Archive,
}

/// <summary>The tenant lifecycle: which states may follow which, which states permit
/// normal tenant operation, and the mapping from named operations. Deny-by-default:
/// an edge not listed here is not a legal transition.</summary>
public static class TenantLifecycle
{
    /// <summary>Allowed transitions, from → the states it may move to. Archived is
    /// absent (terminal). Grace/Suspended can still enter offboarding; Offboarding can
    /// be cancelled back to Active or completed to Archived.</summary>
    private static readonly Dictionary<TenantStatus, IReadOnlySet<TenantStatus>> Edges =
        new()
        {
            [TenantStatus.Provisioning] = Set(TenantStatus.Trial, TenantStatus.Active, TenantStatus.Suspended),
            [TenantStatus.Trial] = Set(TenantStatus.Active, TenantStatus.Grace, TenantStatus.Suspended, TenantStatus.Offboarding),
            [TenantStatus.Active] = Set(TenantStatus.Grace, TenantStatus.Suspended, TenantStatus.Offboarding),
            [TenantStatus.Grace] = Set(TenantStatus.Active, TenantStatus.Suspended, TenantStatus.Offboarding),
            [TenantStatus.Suspended] = Set(TenantStatus.Active, TenantStatus.Offboarding),
            [TenantStatus.Offboarding] = Set(TenantStatus.Active, TenantStatus.Archived),
            [TenantStatus.Archived] = Set(),
        };

    private static HashSet<TenantStatus> Set(params TenantStatus[] states) => new(states);

    /// <summary>True when <paramref name="from"/> may transition directly to
    /// <paramref name="to"/>. A no-op self-transition is not a legal edge.</summary>
    public static bool CanTransition(TenantStatus from, TenantStatus to) =>
        Edges.TryGetValue(from, out var tos) && tos.Contains(to);

    /// <summary>A terminal state has no outgoing transitions.</summary>
    public static bool IsTerminal(TenantStatus status) =>
        Edges.TryGetValue(status, out var tos) && tos.Count == 0;

    /// <summary>Whether a tenant in this state may perform normal tenant-scoped writes
    /// and device/control operations. Provisioning is not yet usable; suspended and the
    /// offboarding/archived states have operations halted (billing/export/support
    /// recovery paths are gated separately, not here).</summary>
    public static bool AllowsOperation(TenantStatus status) =>
        status is TenantStatus.Trial or TenantStatus.Active or TenantStatus.Grace;

    /// <summary>The (from → to) target of a named operation, or null when the operation
    /// is not applicable from that state. Combined with <see cref="CanTransition"/> this
    /// keeps intent and the graph in one place.</summary>
    public static TenantStatus? Target(TenantStatus from, TenantLifecycleOperation op) => op switch
    {
        TenantLifecycleOperation.Activate when from is TenantStatus.Provisioning or TenantStatus.Trial
            or TenantStatus.Grace or TenantStatus.Suspended => TenantStatus.Active,
        TenantLifecycleOperation.EnterGrace when from is TenantStatus.Trial or TenantStatus.Active => TenantStatus.Grace,
        TenantLifecycleOperation.Suspend when from is TenantStatus.Trial or TenantStatus.Active or TenantStatus.Grace
            or TenantStatus.Provisioning => TenantStatus.Suspended,
        TenantLifecycleOperation.Restore when from is TenantStatus.Suspended => TenantStatus.Active,
        TenantLifecycleOperation.BeginOffboarding when from is TenantStatus.Trial or TenantStatus.Active
            or TenantStatus.Grace or TenantStatus.Suspended => TenantStatus.Offboarding,
        TenantLifecycleOperation.CancelOffboarding when from is TenantStatus.Offboarding => TenantStatus.Active,
        TenantLifecycleOperation.Archive when from is TenantStatus.Offboarding => TenantStatus.Archived,
        _ => null,
    };

    /// <summary>The status implied by the legacy (Active, Offboarded) booleans, for
    /// backfill. Offboarded was a terminal flag (reactivation was refused), so it maps
    /// to Archived; otherwise Active/Suspended follow the Active flag. Provisioning/
    /// Trial/Grace/Offboarding have no legacy representation.</summary>
    public static TenantStatus FromLegacy(bool active, bool offboarded) =>
        offboarded ? TenantStatus.Archived
        : active ? TenantStatus.Active
        : TenantStatus.Suspended;
}
