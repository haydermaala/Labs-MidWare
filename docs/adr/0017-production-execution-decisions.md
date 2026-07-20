# ADR 0017 — Production execution decisions (LabConnect at lc.spottiq.com)

- Status: Accepted (Production Phase A gate)
- Date: 2026-07-20

## Context

The production master prompt authorizes building and launching the LabConnect
multi-tenant SaaS control plane at `https://lc.spottiq.com`
(`PRODUCTION_EXECUTION_PLAN.md` is the controlling launch spec). Phase A
discovery captured the account/DNS state and required explicit user
confirmation of provider choices. The user confirmed the following on
2026-07-19/20; these are settled and must not be re-litigated.

## Decisions (user-confirmed)

1. **Repository: continue in the existing public `haydermaala/Labs-MidWare`.**
   No new private repo. Rationale: the monorepo already matches the plan's
   repository specification with a large verified foundation, and public
   visibility keeps GitHub Actions free (a private repo re-introduces the
   Actions spending-limit problem). Consequence: source is publicly visible;
   secrets never enter the repo (enforced by gitleaks + push protection);
   security posture must not rely on source secrecy.
2. **Railway: extend the existing project, keeping the name `LabMidware`.**
   The current environment becomes production; `staging` and `development`
   environments are added in the same project. Consequence: production lineage
   (Postgres, migrations, domain) is preserved; per-environment isolation is
   enforced by separate databases, secrets, and buckets — never shared.
3. **Transactional email: Hostinger/Titan SMTP** behind the provider
   abstraction required by the plan. Sender identity uses the existing Titan
   mail domain (`spottiq.com`); SPF/DKIM already exist at the apex. Consequence:
   no new DNS records needed for launch email; bounce/complaint webhooks are
   unavailable on plain SMTP, so suppression handling is implemented locally
   (hard-fail tracking) and the adapter interface keeps Resend/Postmark as a
   drop-in upgrade when volume or deliverability demands it.
4. **Payments: Stripe test-mode now; live provider decision deferred.** The
   full provider-agnostic billing boundary (plans, checkout, portal,
   entitlements, webhooks, dunning) is built and tested against the Stripe
   sandbox. Live activation (and final pricing/currency/tax) happens only after
   the user confirms business jurisdiction and approves prices — Stripe is not
   available in all countries, so Paddle/merchant-of-record remains the
   fallback at that gate.

## Standing constraints (unchanged)

- DNS: exactly one additive `lc` CNAME in Hostinger at Phase H; apex/`www`/MX/
  SPF/DKIM untouched. Before-state: `docs/operations/dns-before-state.md`.
- Cloudflare R2 per-environment buckets for signed artifacts only; R2
  enablement verified read-only before any bucket is created.
- Clinical/device safety boundaries per `DEVELOPMENT_PLAN Procution.md` §1.2
  and the master prompt: synthetic data only, passive capture default, no
  result release without validated sign-off, no physical analyzer work without
  separate authorization.
- Design source of truth: `PRODUCT.md` + `DESIGN.md` (from the mandated
  `/ui-ux-pro-max` workflow; machine baseline in `design-system/labconnect/`).

## Consequences

Work proceeds through Phases B–H of `PRODUCTION_EXECUTION_PLAN.md` §9 without
re-asking the four settled questions. Remaining mandatory human gates: Stripe/
Paddle live onboarding + pricing approval (Phase E→H), Titan SMTP credential
provisioning (secret only the user can supply), code-signing identities
(deferred), legal page review, production launch report sign-off, and any
physical analyzer work.
