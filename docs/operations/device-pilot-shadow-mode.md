# Device pilot — shadow-mode runbook (Phase I)

Operational how-to for the **first physical-analyzer pilot**, in passive shadow
mode. This runbook is the executable companion to
[`validation-strategy.md`](../validation/validation-strategy.md) (the authority on
*what, why, and who*); it does not restate or override it.

> **This document authorizes nothing.** A pilot may begin only after separate,
> written authorization and a laboratory agreement are in place, the driver/profile
> is at lifecycle state `validated` with software evidence intact, the interface
> documentation is licensed, and the clinical approver is named
> (`validation-strategy.md` §6 preconditions). If any is missing, stop.

## Non-negotiable safety rules (hold for the entire pilot)

1. **Passive capture only.** The gateway observes; it never transmits to the
   analyzer. Capture is device→gateway by construction (ADR 0009,
   `transport-core`). No writes, no queries, no orders — those are a separate,
   later, separately-approved phase.
2. **No connection to the production LIS/HIS.** Shadow mode runs alongside the
   existing validated workflow, not in place of it. The gateway is not the LIS.
3. **No result release to patient records.** Captured results are for
   expected-vs-actual comparison only until the laboratory signs off. Nothing
   reaches a patient chart during shadow mode.
4. **Synthetic / de-identified only leaves the isolated environment.** Any
   capture kept as a fixture is irreversibly de-identified first and carries full
   fixture metadata (`validation-strategy.md` §4). Real captures stay on the
   isolated network.
5. **Unknown analyzer ⇒ capture-only, indefinitely.** No behavior is inferred or
   emulated for an uncharacterized instrument.
6. **Two-person integrity.** Certification and any production change need the
   mapping reviewer **and** the clinical approver, neither being the author
   (`validation-strategy.md` §8).

If a rule cannot be satisfied, the pilot does not proceed.

## Pre-pilot readiness checklist

- [ ] Written authorization + laboratory agreement on file; clinical approver named.
- [ ] Driver/profile at `validated`; software evidence green (the conformance
      cases pass: `cargo test -p conformance`).
- [ ] Isolated analyzer network prepared; analyzer VLAN treated as untrusted;
      **no** route to production LIS/HIS.
- [ ] The gateway host is provisioned and enrolled against the control plane
      (`gatewayd` — see [`../operations/desktop-packaging.md`](desktop-packaging.md)
      for the technician app; enrollment via the console, Fleet → Add gateway).
- [ ] The capture source is decided and is **read-only**: a network TAP / SPAN
      (mirror) port for TCP analyzers, or a copy of the analyzer's outbound file
      drop for file-based ones. The gateway must not sit inline where it could
      alter or block the analyzer↔LIS path.
- [ ] Controlled specimens with **known expected results** are prepared by the
      laboratory (the `validation-strategy.md` §7 matrix).

## Shadow-mode setup

1. **Run the gateway in passive capture mode.** On the gateway host, start the
   daemon pointed at the read-only capture source. It enrolls once, captures
   passively into the durable queue, and reports PHI-free telemetry (counts only)
   to the cloud fleet:
   ```bash
   LC_CONTROL_PLANE_URL=https://lc.spottiq.com \
   GATEWAYD_BOOTSTRAP_TOKEN=<one-time token from the console> \
   GATEWAYD_NAME='pilot-<lab>-<analyzer>' \
   GATEWAYD_CAPTURE_ADDR=127.0.0.1:9600 \
   gatewayd --run
   ```
   For a TCP analyzer, bridge its mirrored outbound stream to the loopback
   capture port with a read-only forwarder (e.g. a one-way `socat` from the SPAN
   capture to `127.0.0.1:9600`); for a file drop, capture the copied directory.
   The daemon is capture-only regardless of source.
2. **Confirm capture-only, provably.** Verify the gateway opens no path back to
   the analyzer (no listening service the analyzer connects *to* for control; the
   capture listener is loopback-only and inbound-data-only). Confirm the existing
   LIS keeps receiving results unchanged — shadow mode must be invisible to it.
3. **Confirm liveness + throughput in the console.** In the LabConnect console
   (`https://lc.spottiq.com`), the pilot gateway shows **online** with a rising
   **captured** count and no **dead** items as specimens run. No result content
   is visible in the cloud — counts only.

## Validation procedure (per §6/§7)

For each specimen in the coverage matrix, side by side with the existing
validated workflow:

1. Run the specimen on the analyzer; the current validated LIS path processes it
   as usual (unchanged).
2. The gateway captures the same message passively.
3. Compare **captured structural result vs the laboratory's known expected
   result** using the conformance harness as the comparison engine: encode the
   captured (de-identified) message as a `Case` and run `run_case` — actual must
   equal expected verbatim (exact decimals, flags, status preserved).
4. Compare **captured vs the existing workflow's result**, result by result.
   Every discrepancy is explained and resolved — never averaged away
   (`validation-strategy.md` §6.5).
5. Record actual-vs-expected for every applicable case (§7). `N/A` only with a
   recorded rationale from the clinical approver.

**Sign-off:** the clinical approver (organizationally separate from engineering)
reviews the evidence and signs the validation report. On sign-off, the driver may
be certified **for that site only**.

## Staged progression — each stage is a separate gate

| Stage | What runs | Gate to enter |
|---|---|---|
| **Shadow (this runbook)** | Passive capture + expected-vs-actual, no release | Preconditions above |
| **Unidirectional** | Validated results delivered to the LIS | Signed shadow validation report; zero unexplained discrepancies; rollback + downtime SOPs rehearsed |
| **Bidirectional** | Orders/queries to the analyzer | A **separate, explicitly recorded** approval — never implied by inbound certification (`validation-strategy.md` §6.8) |

Do not skip or combine stages. Site certification is site-limited; another site
repeats site validation before it is certified there.

## Abort / rollback criteria

Stop the pilot immediately and revert to the existing workflow if any of these occur:
- Any sign the gateway influenced the analyzer or the analyzer↔LIS path (it must
  not — investigate the tap/forwarder wiring).
- An unexplained discrepancy in the side-by-side comparison.
- Any capture leaving the isolated environment without de-identification.
- Loss or duplication of a captured message not explained by the durable-queue
  recovery behavior.

Rollback is trivial by design: the gateway is passive, so **removing it changes
nothing** for the live workflow. Decommission the pilot gateway in the console;
the existing LIS path was never altered.

## Out of scope until separately authorized

- Releasing any real result into a patient record.
- Bidirectional / outbound orders to the analyzer.
- Any compatibility claim beyond the one validated site + instrument + interface
  revision.

## References

- `validation-strategy.md` — clinical validation procedure (§6), coverage matrix
  (§7), roles and dual approval (§8), non-negotiable rules (§10).
- ADR 0009 — passive-capture transport contract. ADR 0011 — driver signing.
- `tests/conformance` — the synthetic expected-vs-actual comparison engine.
- `go-live-checklist.md` — production readiness (the cloud side this reports to).
