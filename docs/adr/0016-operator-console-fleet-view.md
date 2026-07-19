# ADR 0016 — Operator console fleet view + control-plane CORS

- Status: Accepted (Phase 8)
- Date: 2026-07-19

## Context

The control-plane API now exposes tenants, gateway inventory with derived liveness,
and soft lifecycle actions, but nothing surfaced them to a human. Operators need a
console to see the fleet (who is online, when last seen) and to run the lifecycle
actions. Because that console is a browser app on a **different origin** than the
API, the API also needs a cross-origin policy — and for a system handling clinical
infrastructure, that policy must be closed by default, not wide open.

## Decision

- **`control-plane-web` becomes the fleet view.** The Phase-1 shell is replaced with
  a read-then-act operator console: a connection form (control-plane URL + admin
  token, held **in memory only**), a tenant list with active/inactive state and
  deactivate/reactivate, and a per-tenant gateway table showing derived
  `status` (online/offline/never/decommissioned) + last-seen, with a decommission
  action (disabled once inactive).
- **Typed control-plane client in `@lab-connect/api-client`.** Fleet operations are
  a typed module (`control-plane.ts`) beside the existing gateway client, mirroring
  the server DTOs (including the `active` flag, `lastSeenAt`, and `status`). UI
  components are presentational and unit-tested with fixtures; the client is tested
  against an injected `fetch`.
- **CORS is an explicit, closed-by-default allowlist.** The API reads
  `ControlPlane:AllowedOrigins` (comma-separated) and permits **only** those origins,
  and only the `Authorization`/`Content-Type` headers and `GET`/`POST` methods the
  API actually uses. **No credentials** are allowed — auth is a bearer token, not a
  cookie. With no configured origins, cross-origin is blocked. The allowlist is
  evaluated per request against live configuration.

## Consequences

- Operators get fleet visibility and lifecycle control in a browser, reusing the
  liveness/lifecycle work (ADR 0014, ADR 0015) with no new server endpoints.
- The console is safe to deploy on a separate origin only once that origin is
  allowlisted; a misconfigured or unknown origin is refused by default.
- **OPEN:** the console itself is not yet hosted, and it authenticates with the admin
  bearer token — real deployment must put it behind proper operator identity (OIDC,
  still OPEN) rather than a shared admin token, and serve it from an allowlisted (ideally
  same-site) origin. Driver, mapping, and audit views remain future work.

## Alternatives considered

- **Serve the console same-origin from the API** (no CORS): simpler, but couples the
  console's deploy to the API and blocks local development against a remote API.
  A closed allowlist keeps both options open.
- **`AllowAnyOrigin`**: rejected — an open API surface for management/lifecycle calls
  is an unacceptable default even with bearer auth.
- **Put the fleet view in the Tauri technician app**: rejected — that app is the
  edge/site technician surface (capture-only, local gateway); fleet management is an
  operator/back-office concern and belongs in the web control plane.
