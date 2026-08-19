# Admin-token retirement inventory

## Status (2026-08-06) — seven of eight blockers fixed

Recorded here because the previous audit's findings existed only in a chat transcript and
were lost when it was compacted; that is why this re-audit was necessary at all. Findings
live in the repo from now on.

| # | Blocker | Status |
| --- | --- | --- |
| **B1** | Readiness gate measures itself, can never say "ready" | **Fixed** — `break-glass-watch.sh` has two observer modes and prints which one it used. `LC_SESSION` (an Auditor/Security-Admin session) discounts nothing and is sound. Without one it falls back to the token and discounts `platform:platform.security_event.read`, its own footprint — usable, but blind to token reads of the audit log, and it says so on every run |
| **B2** | Regression test guarding the recording is vacuous | **Fixed** — observer is an auditor session; `Observing_The_Trail_Does_Not_Add_To_It` is the control, and a targeted negative control now fails the test it is meant to fail |
| **B3** | Two-party approval bypassable on tenant deactivation | **Fixed** — root cause was B7 |
| **B4** | Same on support grants; duration uncapped | **Fixed** — B7 closes the bypass (`Support_Grants_Cannot_Be_Self_Approved_By_The_Token`); duration capped at `MaxDurationMinutes` = 8h |
| **B5** | Same on tenant offboarding | **Fixed** — B7 closes it; same root cause and mechanism |
| **B6** | `/api/admin/memberships` still live and stronger than its replacement | **OPEN** — see below |
| **B7** | Token chooses its own identity per request (dual identity) | **Fixed** — `CurrentUser` returns null when the admin token is presented, so a break-glass request has exactly one identity |
| **B8** | No test coverage of the post-retirement configuration | **Fixed** — `NoAdminTokenTests` runs the app with the token absent |

Two findings outside the token's scope also came out of this audit and are fixed:

- **Demotion did not demote.** Root-scope role assignments survived `ChangeRole` and
  `RemoveMember` and were unioned back into every authorization decision, so a demoted
  member kept their old authority. No token involved; it affected ordinary tenants and
  would have survived retirement entirely. See `StaleAssignmentTests`, and
  `SupportGrantScopeTests` for the support-grant composition it also reopened.
- **No platform static separation of duty.** One human could hold both
  `platform-support-engineer` and `platform-security-admin` — the requester/approver pair
  — making the dynamic check a formality. `PlatformSeparationOfDuty` now refuses such a
  grant at the only path that confers a platform role.
- **Cross-class test pollution.** Every test host shared one in-memory database, so any
  assertion about a global append-only table was really measuring the rest of the suite.
  `IsolatedApiFactory` gives such tests their own.

**B6 is the one open item.** `POST /api/admin/memberships` and `POST /api/admin/users` are
still mapped. They are token-only, so they close the moment the variable is unset — but
until then the legacy membership route has no `HasActiveOwner` guard and is therefore
strictly more powerful than the endpoint credited with replacing it. Deleting them is
entangled with 27 test call sites that use them for setup, so it belongs with the
retirement step rather than ahead of it.

**The gate for retirement is unchanged and is not a code change:** a Root Owner login must
be provably working before the variable is unset. `NoAdminTokenTests
.Retiring_The_Token_Requires_A_Root_Owner_To_Already_Exist` pins why — with the token gone,
platform roles can only be granted by someone who already holds one, and the application
has no recovery path if every Root Owner is lost.

Run the check with:

```bash
SINCE=2026-08-06T10:00:00Z scripts/break-glass-watch.sh production 30
```

Set `LC_SESSION` first for the sound reading — sign in as an Auditor or Security Admin and
copy the `lc_session` cookie from DevTools > Application > Cookies > lc_session > Value
(it is HttpOnly, so `document.cookie` cannot read it — the DevTools cookie inspector can). Without it the script
still runs, in the degraded token mode described in B1. Exit codes: `0` ready, `1` in use
or inconclusive, `2` no verdict reached. `2` is deliberately distinct from `1` — a checker
that fails must not be mistakable for a checker that found the token in use.

**`SINCE` is where the retirement clock starts, and it matters here.** The migration and
verification work of 2026-08-06 used the token legitimately, and those uses are in the
append-only trail permanently — so a 30-day window run today reports "in use" on the
strength of work already accounted for, which tells you nothing and trains you to ignore
the gate. Set `SINCE` once to the moment the clock starts and leave it; the effective
cutoff is the later of `now - days` and `SINCE`, so it can only narrow the window, never
widen it. It is also the one knob that could manufacture a green result, so it is never
silent: excluded uses are counted in the output and listed individually in the verdict.

The known pre-clock uses as of 2026-08-06 are all verification calls made while proving
the audit recording worked: `platform.whoami`, `platform:platform.tenant.read`, and
`ten_43eb2e9b…:fleet.gateway.view`.

---

Scope: `ControlPlane:AdminToken` / `ControlPlane__AdminToken`. Audited against the tree at
`integration/p1-p2-p3` (`b257415`). `services/control-plane-api/Program.cs` is **1,745 lines**
and registers **87** `app.Map*` endpoints (85 routes + 2 fallbacks), gated by 30 `Forbidden(`
sites, 24 `PlatformForbidden(` sites, and 7 direct `BreakGlass(` sites.

Confidence is marked on every finding:

| Mark | Meaning |
| --- | --- |
| **EXEC** | Reproduced by executing the path against the real app (WebApplicationFactory, in-memory store). |
| **READ** | Established by reading the code; not executed. |
| **INFER** | Composition of two separately verified halves; the composed path was not executed. |

Probe suite used for EXEC findings: 10 probes, all green. Kept out of the repo at
`/private/tmp/claude-501/-Users-haydermaala-My-Drive-TTECH-Labs-MidWare/e3845464-3106-467e-a90f-5c335e6102ed/scratchpad/ZzAuditProbeTests.cs`.
Repo left clean; `dotnet test` = **306 passed, 0 failed**.

---

## 1. Verdict

**The token can be retired, and the retirement mechanism itself is safe — but not yet, because
the instrument that is supposed to authorize the decision cannot produce the answer it exists to
produce.** Every function that has no session-based path has a shipped replacement: the four
token-only routes (`POST`/`GET /api/tenants`, `POST /api/admin/memberships`,
`POST /api/admin/users`) are each covered by a `/api/platform/*` endpoint, so a proven Root Owner
is not missing a capability. Unsetting the variable also fails closed by construction —
`IsAdmin` is `!string.IsNullOrEmpty(adminToken) && …` (Program.cs:203-205), so an empty value
turns every `BreakGlass` call into `false` (Program.cs:220-229) and simultaneously closes every
defect below. What blocks the decision is evidence, in two layers. First,
`scripts/break-glass-watch.sh` reads `/api/platform/security-events` **using the token**
(break-glass-watch.sh:51-52); that read passes through `PlatformForbidden`, whose first statement
records a `platform.break_glass.used` row synchronously (Program.cs:1262 → :227 →
PlatformAuditService.cs:23-35, `SaveChanges()` at :34) *before* the handler reads the log at
Program.cs:1076-1081 — so the response always contains the event the script is looking for,
`in_window` is never empty, and the verdict is permanently `in-use`/exit 1 (**EXEC**, Probe 4).
The gate added specifically to authorize retirement is structurally incapable of authorizing it.
Second, the regression test that guards the recording the gate depends on is vacuous: its
observer function is itself a token call (BreakGlassAuditTests.cs:38-46), so
`Assert.True(after.Count > before)` at :60 passes even when the endpoint under test records
nothing (**EXEC**, Probe 8). Separately, and independent of the evidence problem, leaving the
token set has an ongoing cost the shipped work was credited with removing: the token still
defeats **three** distinct two-party controls end-to-end (tenant deactivation, support-access
grants, irreversible tenant offboarding) using itself as the only credential, and it still
reaches a legacy membership endpoint that is strictly more powerful than the guarded replacement
it was said to be replaced by.

---

## 2. Surviving blockers

Ordered by severity. "Blocker" here means: something that must be built or fixed before the
retirement decision can be made honestly, or something that makes leaving the token set unsafe.

| # | Location | What is actually wrong | What must be built | Conf. |
| --- | --- | --- | --- | --- |
| **B1** | `scripts/break-glass-watch.sh:47-52`; Program.cs:1262, :227; PlatformAuditService.cs:23-35; Program.cs:1076-1081 | The readiness gate authenticates with the token, so its own read writes a `platform.break_glass.used` row into the window it then measures. `SaveChanges()` is inline and committed before the handler reads. `in_window` ≥ 1 always → verdict `in-use` → exit 1, forever. The gate can never say "ready". | Read the log with a **platform-role session** (Security Admin or Auditor both hold `platform.security_event.read`, PlatformRoles.cs:62-70), not the token. Fall back to a direct DB query if no session is available. Then exclude nothing — an honest observer perturbs nothing. | **EXEC** (Probe 4) |
| **B2** | `services/control-plane-api.Tests/BreakGlassAuditTests.cs:38-46`, :54-63 | `BreakGlassEvents()` says "Read with a client that does NOT use the token" and then calls `Admin()`, which does. Both `before` and `after` are measured by token calls, so `after.Count` is `before + 2` regardless of line 56. `Assert.True(after.Count > before)` (:60) cannot fail; `Assert.Contains(…StartsWith("platform:"))` (:63) is satisfied by the observer's own `platform:platform.security_event.read` event. The test written so the PlatformForbidden regression "cannot happen again" would not catch that regression. | Make the observer a platform-role session (the comment already describes the right design — only the implementation is wrong). `Bootstrap_Endpoints_Record_Break_Glass` (:78-93) has the same defect but is carried by its `Assert.Contains` on specific details; the fix is the same. | **EXEC** (Probe 8) |
| **B3** | Program.cs:1132, :1138-1142, :1199; :664-680, :689-701; ApprovalService.cs:100; CurrentUser at Program.cs:1087-1099 | **Two-party approval on `tenant.tenant.deactivate` is bypassable with the token as the sole credential.** The single-call bypass is genuinely closed (`ApprovalGranted: approvalSatisfied && !viaSupportGrant`, :1199 → 403). The multi-call one is not: (1) `POST /api/admin/users` create an account with a chosen password; (2) `POST /api/auth/login`; (3) `POST /api/tenants/{id}/approvals` with the token and **no** cookie → requester `"platform-admin"` (:675); (4) `POST …/approve` with the token **and** the new account's `lc_session` cookie → approver is the new user (:698), because `CurrentUser` falls through to the cookie when the header is not `Bearer ses_…` (:1095). `IsDistinctParty` passes, `PerformApproved` (:655-661) deactivates the tenant. The second party needs no membership and no role anywhere. Program.cs:1130-1131 — "no credential should be able to stand in for the second party" — is not true as shipped. | Break the dual identity: when `IsAdmin(req)` is true, `CurrentUser` must return `null` (or `Forbidden`/`PlatformForbidden` must pass the already-resolved actor down to the handler instead of each handler re-deriving it 19 times). Additionally, an approval whose requester is `platform-admin` should not be approvable, and vice versa. | **EXEC** (Probe 2) |
| **B4** | Program.cs:894-906, :915-926; PlatformSupportService.cs:40, :64, :100-107 | Same shape on **support-access grants**, which never received the `ApprovalGranted` treatment at all. Token + patsy cookie requests the grant; token alone approves it. The patsy — a user with no membership anywhere — then holds live cross-tenant read access via `HasActiveGrant` (Program.cs:1168-1174). Duration is caller-controlled and **uncapped**: Program.cs:904 passes `body.DurationMinutes ?? 60` straight through and PlatformSupportService.cs:40 only floors it (`<= 0 ? 60 : …`). A 5,256,000-minute (10-year) grant is accepted. One break-glass session leaves behind a decade-long cross-tenant path attributed to someone else that produces **no further** break-glass events when used. | Cap `RequestedDurationMinutes` server-side (a hard ceiling, e.g. 8h, plus plan/role policy). Apply the same distinct-party hardening as B3. Consider requiring the requester to be a real session. | **EXEC** (Probes 5, 9) |
| **B5** | Program.cs:938-949, :958-984; PlatformOffboardService.cs:62 | Same shape on **tenant offboarding** — the irreversible one. Token + patsy cookie opens the request; token alone approves; `TransitionTenant(…BeginOffboarding…)` runs (:970). `PlatformPermissions.TenantOffboard` is `RiskLevel.Critical` with `requiresMfa: true, requiresFreshAuth: true` (PlatformPermissions.cs:34-36) and is documented as "two-party approval". | As B3. This is the highest-consequence instance and should be fixed first among the three. | **EXEC** (Probe 10) |
| **B6** | Program.cs:405-411 vs :808-829; Memberships.cs:148-156 | `POST /api/admin/memberships` is **not** replaced — it is still mapped and still token-gated, and it is strictly **more powerful** than the endpoint credited with replacing it. The platform version guards with `members.HasActiveOwner(tenantId)` → 409 (:814-820); the legacy one has no such guard (:405-411). Verified directly: on a tenant that already has an active owner, `/api/platform/tenants/{id}/memberships` returns **409** and `/api/admin/memberships` returns **204** for the same intruder. The tighter control the shipped work is credited with — "rescues a stranded tenant, does not let a platform operator insert themselves into a working one" (Program.cs:804-806) — is bypassed by an endpoint one path segment away. | Delete `POST /api/admin/memberships` (Program.cs:405-411) and `POST /api/admin/users` (:1326-1341), or make them 410 Gone. Migrate the 9 test call sites for the former and 18 for the latter onto the platform endpoints or a test-only seam. | **EXEC** (Probe 6) |
| **B7** | Program.cs:1087-1099 (`CurrentUser`); the 19 `?? "platform-admin"` sites at :470, :600, :631, :675, :698, :711, :755, :795, :826, :902, :919, :932, :946, :962, :990, :1005, :1028, :1053, :1063 | The token **chooses its own audit identity per request**. `IsAdmin` matches on the exact header (:203-205) while `CurrentUser` falls through to the `lc_session` cookie (:1095), so one request can be god-mode *and* resolve to an arbitrary user at every handler-level identity derivation. Verified: `POST /api/platform/users` carrying the token + an innocent user's cookie writes `platform.user.created` attributed to the **innocent user** (:795-796), while the paired `platform.break_glass.used` records `platform-admin` (:227). The two rows disagree and only one names the token. This is the root cause of B3/B4/B5, and it also corrupts attribution for role grants (:755), membership seeding (:826), exports (:1053), legal holds (:1028) and archival (:1005). | Single fix, wide blast radius: make `IsAdmin(req)` and `CurrentUser(req, …)` mutually exclusive. This is the highest-leverage change in the list. | **EXEC** (Probe 3) |
| **B8** | `services/control-plane-api.Tests/` — 18 files set `["ControlPlane:AdminToken"] = "test-admin"`; only `HealthEndpointTests.cs` does not | **There is no test coverage of the post-retirement configuration.** Not one test exercises the app with the token absent, so the behaviour you are about to ship to production — every `BreakGlass` returning false, the four token-only routes 401ing, `ActorRole` never returning `Roles.Owner` — is entirely unverified. 18 test files use `/api/admin/users` and 9 use `/api/admin/memberships` as their setup path; 24 sites call the token-only `"/api/tenants"`. | A `NoAdminTokenFactory` fixture plus a suite asserting: the 4 token-only routes 401; a Root Owner session performs every equivalent operation; `Forbidden`/`PlatformForbidden` fall cleanly to the session path. This is the actual gate for Step D. | **READ** |

---

## 3. Everything else, by category

Not blockers. Each already has a named replacement or is cosmetic/documentation debt.

### 3.1 Token-only routes — all four have shipped replacements

| Legacy route | Gate | Replacement | Replacement's gate |
| --- | --- | --- | --- |
| `POST /api/tenants` (Program.cs:277-283) | `BreakGlass(req, "tenant.create")` :279 | `POST /api/platform/tenants` (:841-848) | `PlatformPermissions.TenantProvision` — Root Owner + Operations Admin (PlatformRoles.cs:44-49) |
| `GET /api/tenants` (:285-286) | `BreakGlass(req, "tenant.list")` :286 | `GET /api/platform/tenants` (:737-742) | `TenantRead` — every platform role |
| `POST /api/admin/memberships` (:405-411) | `BreakGlass(…":admin.membership.grant")` :407 | `POST /api/platform/tenants/{tenantId}/memberships` (:808-829) | `MembershipSeed`, Root-Owner-only, + `HasActiveOwner` 409 (:814) — **see B6, the legacy route is still live and unguarded** |
| `POST /api/admin/users` (:1326-1341) | `BreakGlass(req, "admin.user.create")` :1328 | `POST /api/platform/users` (:778-798) | `UserCreate`, Root-Owner-only (PlatformPermissions.cs:57-60) — **still live, see B6** |

`GET /api/platform/whoami` (:722-735) also accepts the token but has a full session fallback
(:729-734), so it is not token-only.

### 3.2 Documented risk acceptances — correct as designed, worth restating

| Item | Location | Note |
| --- | --- | --- |
| Break-glass asserts MFA + fresh auth rather than enforcing them | Program.cs:1190-1191 (tenant), :1286-1287 (platform) | `MfaSatisfied: breakGlass \|\| …`, `FreshAuth: breakGlass \|\| …`. So `UserCreate` and `MembershipSeed` — both `requiresMfa: true, requiresFreshAuth: true` (PlatformPermissions.cs:57-73) — are satisfied by presenting a static string. Deliberate and documented (:1259-1261, :1127-1131): the token is an out-of-band pre-shared secret. Means the "MFA + fresh auth" in the shipped-step description describes the **session** path only. **READ** |
| Break-glass no longer skips `RequiresApproval` | Program.cs:1199 | Confirmed working for the single-call case: `POST /api/tenants/{id}/deactivate` with the token returns **403**. **EXEC** (Probe 1) |
| Break-glass runs as `Roles.Owner` in the tenant gate | Program.cs:1141 | Confirmed. Owner holds `ManageTenant` (Permissions.cs:157), which reaches `TenantDeactivate` (:103-109). |
| Support grants confer read-only | Program.cs:1168-1174 | `role = Roles.ReadOnly` and only after membership resolution. Correct as written — but see §5 on the stale-assignment interaction. |

### 3.3 Test-harness dependency

| Item | Count | Replacement |
| --- | --- | --- |
| `/api/admin/users` as test setup | 18 sites across 11 files | `POST /api/platform/users`, or a test-only seeding seam |
| `/api/admin/memberships` as test setup | 9 sites across 7 files | `POST /api/platform/tenants/{id}/memberships` (needs the ownerless precondition) |
| `"/api/tenants"` (token-only create/list) | 24 sites | `POST`/`GET /api/platform/tenants` |
| Factories setting `test-admin` | 18 of 19 | See B8 |

### 3.4 Operational + client surface

| Item | Location | Status |
| --- | --- | --- |
| `scripts/staging-smoke.sh` requires `ADMIN_TOKEN` | :21, :33, :45 | Real token dependency in ops tooling. All paths it exercises (:84-88) are tenant endpoints with session equivalents — migratable to an operator session. **READ** |
| `scripts/break-glass-watch.sh` requires the token | :47-52 | See B1. |
| SPA option field is *named* `adminToken` but carries the **session** token | `packages/api-client/src/control-plane.ts:65`, :71; `platform.ts:54`; `apps/control-plane-web/src/auth/AuthProvider.tsx:31-32`; `Pages.tsx:18-20` | Naming debt, not an active dependency — every caller passes the operator session token. The doc comment "Admin bearer token; required for every management call" (control-plane.ts:65) is stale and misleading. Rename to `bearerToken`. **READ** |
| `listTenants` / `createTenant` exported against the token-only routes | `control-plane.ts:105-112` | Dead client surface — no SPA caller. Delete alongside B6. **READ** |
| Cutover doc still instructs capturing the id from `POST /api/admin/users` | `docs/operations/p1-p7-production-cutover.md:285` | Stale; the platform endpoint now exists. |
| Step D checklist describes retirement as "move to break-glass only" | `docs/operations/p1-p7-production-cutover.md:309-311` | Accurate but weaker than the sequence in §6; it does not name the gate. |

---

## 4. Claims refuted during verification

These were asserted in the prior audit pass and do **not** hold against the current tree. Listing
them matters because two of them would have redirected effort away from the real defects.

| # | Claim as made | What the tree actually shows | Correct category |
| --- | --- | --- | --- |
| R1 | "Program.cs is 8,279 lines"; `adminToken` "is in scope for the whole 8,279-line file". | Program.cs is **1,745 lines**. The scope observation about `adminToken` (declared at :178, in scope to `app.Run()` at :1645) is correct; the magnitude is not. Individual line citations in that pass were largely accurate. | Factual error, immaterial to the finding. |
| R2 | "The break-glass elevation clause never fires, so the one control written to make Root Owner high-friction is dead." | Inverted. `grantedByNonBreakGlass` (PlatformAuthorization.cs:68-70) is `false` **precisely for** the Root-Owner-only permissions — `UserCreate`, `MembershipSeed`, `RoleManage` — since only `PlatformRoles.RootOwner` grants them (PlatformRoles.cs:41 vs :44-73). The clause **does** fire for them. It is immaterial either way because `MfaSatisfied`/`FreshAuth` are asserted `true` at Program.cs:1286-1287. And the control is emphatically *not* dead for real Root Owners — the cutover doc documents operators hitting it every 10 minutes (`p1-p7-production-cutover.md`, Step C). | Wrong mechanism. The real (and already-documented) issue is the assertion at :1286-1287 → §3.2. |
| R3 | "The token holds `SupportRequest` and `SupportApprove` simultaneously — a standing **static-SoD violation**, the exact pair `PlatformSupportService.cs:3-4` says must be distinct parties." | There is no platform static-SoD ruleset to violate. `SodRuleEntity` is tenant-scoped and is read only by `RoleGrantService.ActiveRules(db, tenantId)` (RoleGrantService.cs:304-308), which the platform gate never calls. `PlatformSupportService.cs:3-4` describes the **dynamic** rule (requester ≠ approver, enforced at :64). | Recategorized: not a static-SoD violation but a **dynamic**-SoD bypass — which is real and is counted as **B4**. Separately: the absence of any platform static-SoD enforcement is an unflagged gap → §5. |
| R4 | "An operator pasting the god-mode token into the SPA's `adminToken` field turns the whole console into a break-glass client." | Speculative. `AuthProvider.tsx:31-32` supplies `sessionToken`; `Pages.tsx:18-20` documents the field as the operator's session token; no code path passes the god-mode token. | Naming footgun → §3.4, low. Not a defect. |
| R5 | `scripts/break-glass-watch.sh:55` is the token-authenticated fetch. | The fetch is at **:51-52**; :55 is the `trap` for the temp file. | Citation error. The finding itself is correct and is **B1**. |
| R6 | "20 independent re-derivations of `?? "platform-admin"`"; "six ActorRole call sites (451, 458, 466, 602, 633)". | **19** re-derivations (enumerated in B7) and **5** `ActorRole` call sites — the five listed. | Counting errors, immaterial. |

---

## 5. Open questions — all five resolved (2026-08-06)

The audit left five residues. Each is now closed, and each closure was checked with a
negative control: the fix was reverted and the new test confirmed to fail. A guard never
seen to fail is not evidence.

1. **Stale root role-assignments survived demotion and removal.** Confirmed and fixed.
   `ChangeRole`/`RemoveMember` now revoke root-scope assignments in the same transaction
   (`Memberships.RevokeRootAssignments`). Root scope only — a sub-scope grant is a
   different intent and must survive a tenant-wide demotion. `StaleAssignmentTests` asserts
   both directions. This was the most serious finding in the audit and had nothing to do
   with the token: it needed no privileged credential and would have survived retirement.

2. **The support-grant × stale-assignment composition** was INFER; it is now EXEC.
   `SupportGrantScopeTests` runs the whole path: a member with a stale root `tenant-admin`
   assignment is removed, granted an approved support grant, and must get read-only.
   Reverting the `RemoveMember` half makes it fail with the removed member successfully
   creating an invitation — so the composed escalation was real, and is closed.

3. **No platform static separation of duty.** Fixed. `PlatformSeparationOfDuty` declares
   the conflicting pairs and `PlatformAdminService.Grant` refuses a role that would newly
   breach one, returning 409 with the rule name and auditing the refusal as
   `platform.role.grant_refused_sod`. One pair today: support-grant request vs approve.
   Tenant offboarding is deliberately absent — both its sides are gated by the same
   permission, so there is no pair to separate and its protection is dynamic SoD alone.
   Only NEW breaches block, so adding a rule cannot freeze existing accounts out of
   unrelated grants, and Root Owner is exempt as the documented break-glass role.

4. **The structural guard had two holes.** Fixed, and split into three tests that each
   scan the whole service rather than `Program.cs` alone:
   `No_Unaudited_IsAdmin_Call_Sites` (a gate added in a new file is now visible),
   `No_Inline_Reimplementation_Of_The_Token_Check` (the token VALUE may be touched only
   where it is read from configuration and inside `IsAdmin`, so an inline
   `== $"Bearer {adminToken}"` cannot authorize without recording), and
   `Every_ActorRole_Caller_Has_Already_Passed_Forbidden`, which enforces the premise the
   `ActorRole` exemption rests on instead of asserting it in a comment.

5. **Whether the production token begins with `ses_`.** Checked against the live value
   without exposing it: it does not (48 chars). So `CurrentUser` took the cookie branch
   exactly as the B3/B7 analysis assumed, and the dual-identity fix applies as reasoned.

## 6. Retirement sequence

Each step names its gate. Do not proceed past a gate that is not green.

| Step | Action | Gate |
| --- | --- | --- |
| **1** | Fix the observer. Change `scripts/break-glass-watch.sh:47-52` to authenticate with a **platform-role session** (Security Admin or Auditor). Fix `BreakGlassAuditTests.BreakGlassEvents()` (:38-46) the same way. | The vacuity probe fails: two consecutive observations with no endpoint call between them show **no** increase. Then `Platform_Gate_Records_Break_Glass` still passes for the right reason — confirm by temporarily reverting `PlatformForbidden` to short-circuit and watching it go **red**. |
| **2** | Close the dual identity. Make `CurrentUser` return `null` when `IsAdmin(req)` is true, or thread the gate-resolved actor into handlers instead of 19 independent re-derivations (Program.cs:470…:1063). | Probes 2, 3, 9 and 10 all flip to failing. Full suite still 306/306. |
| **3** | Cap support-grant duration server-side in `PlatformSupportService.Request` (PlatformSupportService.cs:40) and reject an out-of-range `DurationMinutes` at Program.cs:904. Audit existing `PlatformSupportGrants` rows for absurd `ExpiresAt` values and revoke them. | A 10-year request is rejected (or clamped) and no live grant in the database exceeds the cap. |
| **4** | Delete `POST /api/admin/memberships` (Program.cs:405-411) and `POST /api/admin/users` (:1326-1341). Migrate the 27 test call sites and delete `listTenants`/`createTenant` (`control-plane.ts:105-112`). Optionally delete the two token-only `/api/tenants` routes (:277-286) once §3.1's replacements carry the 24 test sites. | Probe 6 flips to failing (no unguarded seeding path remains). Full suite green. `grep -rn "/api/admin/" services/ packages/ apps/ scripts/ docs/` returns nothing but changelog. |
| **5** | Add the token-absent test fixture and suite (B8). | With `ControlPlane:AdminToken` unset: every remaining token-only route 401s, a Root Owner session performs every §3.1 operation, and `Forbidden`/`PlatformForbidden` fall to the session path. This suite is the real precondition for Step 7. |
| **6** | Migrate `scripts/staging-smoke.sh` (:21, :33, :45) onto an operator session. Update `docs/operations/p1-p7-production-cutover.md:285` and :309-311. | Staging smoke passes with no `ADMIN_TOKEN` in the environment. |
| **7** | Run the now-honest `break-glass-watch.sh production 30`. Discard any pre-Step-1 history as contaminated — start the window from the Step 1 deploy. | Exit **0**, over a window that begins after Step 1 and in which operator workflows were genuinely exercised. A quiet window on an idle system is not evidence (the script says so at :108-109). |
| **8** | Unset `ControlPlane__AdminToken` in staging. Soak. | Staging serves normally for the soak period; no 401 spike; the platform console works for a Root Owner. |
| **9** | Unset `ControlPlane__AdminToken` in production. Keep the value sealed offline, rotated, for the re-set-and-redeploy path. | `/health/ready` green; a Root Owner session performs a live end-to-end platform operation. |
| **—** | Separately and not gated on any of the above: fix the stale root-assignment retention (§5.1). | A demoted member is refused the owner-only action; a removed member's assignments are revoked. |

**Rollback for steps 8-9:** re-set the variable and redeploy. `IsAdmin` reads a captured
`app.Configuration` value at startup (Program.cs:178), so the change requires a restart in both
directions — budget for that in the cutover window rather than expecting a live toggle.
