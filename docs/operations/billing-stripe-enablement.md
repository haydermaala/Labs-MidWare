# Enabling Stripe billing (test-mode first)

LabConnect's billing is provider-agnostic behind `IBillingProvider`. Until a
Stripe secret key is configured, every environment runs the **fake provider**:
plans, entitlements, server-side gateway quota (402), checkout/portal redirects,
and signed webhooks all work deterministically for development and tests. The
Stripe adapter (`StripeBillingProvider`) activates **only** when `Stripe:SecretKey`
is present, and nothing else changes in the app.

This runbook is the human-participation gate: the steps below require secrets and
dashboard actions that only the account owner can perform. **Use Stripe test-mode
keys first** — never live keys until pricing is approved and the flow is verified.

## What the app expects (configuration)

Set these as environment variables on the Railway service (double-underscore maps
to the `:` config path). Values live only in Railway + your password manager —
never in the repo, commits, or logs.

| Config key | Env var | What it is |
| --- | --- | --- |
| `Stripe:SecretKey` | `Stripe__SecretKey` | Test-mode secret key (`sk_test_…`). Presence flips the provider to Stripe. |
| `Stripe:WebhookSecret` | `Stripe__WebhookSecret` | Signing secret of the webhook endpoint (`whsec_…`). |
| `Stripe:Prices:pilot` | `Stripe__Prices__pilot` | Price id (`price_…`) for the Pilot plan. |
| `Stripe:Prices:laboratory` | `Stripe__Prices__laboratory` | Price id for the Laboratory plan. |
| `Stripe:Prices:network` | `Stripe__Prices__network` | Price id for the Network plan. |
| `ControlPlane:PublicBaseUrl` | `ControlPlane__PublicBaseUrl` | Already set; used for checkout success/cancel + portal return URLs. |

Trial is the free default and has no price. Pricing **amounts** are set on the
Stripe prices, not in this app (the pricing gate: no amounts live in code).
Publishing customer-facing prices still needs explicit approval.

## Steps (in the Stripe Dashboard, test mode)

1. Toggle **Test mode** on.
2. **Products & prices**: create a product per paid plan (Pilot, Laboratory,
   Network) with a recurring price. Copy each `price_…` id into the vars above.
3. **Developers → API keys**: copy the **test** secret key into `Stripe__SecretKey`.
4. **Developers → Webhooks → Add endpoint**:
   - URL: `https://<env-host>/api/billing/webhook`
     (staging: `https://labs-midware-staging.up.railway.app/api/billing/webhook`).
   - Events: `customer.subscription.created`, `customer.subscription.updated`,
     `customer.subscription.deleted`.
   - Copy the endpoint's **Signing secret** into `Stripe__WebhookSecret`.
5. Save the Railway variables and let the service redeploy.

## Verifying live (test-mode)

- `GET /api/billing/plans` still returns the tiers (no prices) — unchanged.
- As a billing manager, **Choose <plan>** on the Billing page → redirects to a
  Stripe Checkout page. Pay with test card `4242 4242 4242 4242`, any future
  expiry/CVC.
- Stripe fires `customer.subscription.created`; the webhook applies it. The
  Billing page then shows the new plan/status and the gateway quota rises.
- **Manage billing** opens the Stripe customer portal for that customer.
- Idempotency: the endpoint records each Stripe event id in `billing_events`
  (UNIQUE); Stripe's retries and duplicate deliveries are no-ops.

## Rollback

Remove `Stripe__SecretKey` (and redeploy) to fall straight back to the fake
provider. Existing subscription rows are untouched; entitlements continue to be
computed from them.

## Notes on the mapping

- The tenant id and plan id ride on the Checkout session's
  `subscription_data.metadata`, so webhook events resolve to a tenant without a
  customer→tenant lookup table.
- Subscription status is normalized to our vocabulary
  (`trialing`/`active`/`past_due`/`canceled`); `incomplete`/`unpaid`/`paused`
  are treated as **not entitled** so a failed payment never grants access.
