# RLS staging smoke checklist

Run this against **staging after** it is deployed with `DATABASE_URL` → `app_runtime`
and `MIGRATION_DATABASE_URL` → owner (see [rls-rollout.md](rls-rollout.md) Steps 1–2).
Its job is not "do endpoints respond" but "does the app behave correctly under
`FORCE ROW LEVEL SECURITY` as the least-privilege role" — i.e. **no missed
`SET LOCAL`**. The two failure signatures to hunt for:

- a write that returns **500** with `new row violates row-level security policy` in
  the logs (a missed scope on a write path), and
- a read that returns **empty** where data exists (a missed scope on a read path —
  RLS fails *closed*, so a bug looks like "no data", not an error).

A quick pass is scripted: `STAGING_URL=… ADMIN_TOKEN=… scripts/staging-smoke.sh`.
The manual two-tenant isolation check below is the one that actually proves RLS.

## Preconditions

- [ ] Staging deployed from `feat/p1-rls-foundation`; `/health/ready` is green
      (proves the runtime `app_runtime` role can reach the DB at all).
- [ ] You have: the staging base URL, the admin bearer token, and a normal
      operator login (email + password) that is a member of at least one tenant.

## 1. Identity (the first thing RLS would break)

`AuthService` writes identity audit rows under a `platform` sentinel tenant, so a
missed scope here breaks login itself.

- [ ] `POST /api/auth/login` with the operator account → **200** + a session token.
- [ ] `GET /api/auth/me` with that token → **200** (the user).
- [ ] `GET /api/me/memberships` → **200** with the expected tenants (this is the
      `UserScope` self-read path — an empty list where memberships exist is a red flag).

## 2. Tenant-scoped reads return data (not empty)

For a tenant the operator belongs to (`{T}`), with the session token:

- [ ] `GET /api/tenants/{T}/settings` → **200** with the tenant.
- [ ] `GET /api/tenants/{T}/gateways` → **200**; if the tenant has gateways, the
      list is **non-empty** (empty here = a missed `TenantScope` on `GatewaysFor`).
- [ ] `GET /api/tenants/{T}/audit` → **200**, **non-empty** (there is always prior
      audit; empty = missed scope).
- [ ] `GET /api/tenants/{T}/billing` → **200** with the plan/entitlements.
- [ ] `GET /api/tenants/{T}/members` (as an owner/tenant-admin) → **200**,
      **non-empty** (includes at least the operator).

## 3. Tenant-scoped writes succeed

- [ ] `POST /api/tenants/{T}/enrollment-tokens` (as fleet manager) → **200** token
      (exercises `IssueBootstrapToken` write under scope).
- [ ] Optionally publish a config / rename the tenant → **200** (write paths).
- [ ] No `row-level security policy` errors appear in the logs for any of the above.

## 4. Device plane (device-auth policies)

Using the token from step 3 with the gateway CLI or curl:

- [ ] `POST /api/gateways/enroll` with the bootstrap token → **200** (gateway id +
      credential). This exercises the `bootstrap_tokens` device-auth policy +
      writing the gateway/credential under the resolved tenant.
- [ ] `POST /api/gateways/heartbeat` with `X-Gateway-Id` + `X-Gateway-Credential`
      → **204** (the `device_credentials` device-auth policy + tenant-scoped update).
- [ ] `GET /api/gateways/config` with the same headers → **200/204** (not 500).

## 5. Cross-tenant isolation — the actual RLS proof

Pick a tenant `{X}` the operator is **not** a member of (use two seeded test
tenants/users if needed):

- [ ] `GET /api/tenants/{X}/gateways` as the operator → **401** (indistinguishable
      from "no such tenant"), **never** another tenant's rows.
- [ ] `GET /api/tenants/{X}/audit` → **401**.
- [ ] If you have two operators in two tenants, confirm each sees only their own
      gateways/members/audit and never the other's — this is the isolation `FORCE
      RLS` is here to guarantee.

## 6. Log scan

- [ ] Grep the staging app logs for `row-level security` — expect **zero**.
- [ ] Grep for `500` on `/api/*` — expect none attributable to authz/RLS.
- [ ] Let real staging usage run for the agreed soak; re-scan before promoting.

## Sign-off

- [ ] Every read in §2 returned data (nothing fell closed).
- [ ] Every write in §3–§4 succeeded with no RLS policy errors.
- [ ] Cross-tenant reads in §5 were refused (401), never leaked.
- [ ] No `row-level security` errors across the soak window.

Only with all four checked do you proceed to the production cutover
([rls-rollout.md](rls-rollout.md) Step 5). Any failure → fix the missing scope on
the branch, redeploy staging, restart the soak.
