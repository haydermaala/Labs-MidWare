# deploy/railway — control-plane staging (config-as-code)

Railway hosts the **development/staging** control-plane API, PostgreSQL, and (later)
a worker. The edge gateway is **never** hosted here — it runs onsite.

## What's here
- [`railway.json`](railway.json) — build (from the control-plane Dockerfile),
  health check (`/health/ready`), and restart policy. Config-as-code, not a
  provisioned service.
- The container image is [`services/control-plane-api/Dockerfile`](../../services/control-plane-api/Dockerfile)
  (verified to build and serve health locally).

## ⛔ Provisioning requires explicit, resource-named approval

Nothing here creates a Railway project, service, database, or environment. Per the
project authorization rules, creating those is an external write that needs a
specific approval **naming the resource and action** (a logged-in browser session
is not authorization). When authorized, the plan calls for:

- Separate environments: `development`, `staging` (production only after
  compliance/hosting review).
- PostgreSQL as a managed service; **migrations run as controlled jobs**, not on
  boot.
- Private networking; health checks; resource/budget alerts; backups + restore
  tested; rollback.
- Secrets injected from Railway's secret store — never committed. `.env.example`
  holds names only.
- Synthetic/de-identified data only until data-governance sign-off.

**Required from the owner before provisioning:** the Railway project name, the
environment(s) to create, the Postgres plan/size, and the budget/alert threshold.
