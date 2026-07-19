# deploy/cloudflare — R2 artifact storage (config-as-code)

Cloudflare R2 stores **encrypted, lifecycle-managed artifacts**: signed driver
packages, synthetic fixtures, release assets, and approved diagnostic bundles.

## What's here
- [`r2-lifecycle.json`](r2-lifecycle.json) — template lifecycle rules
  (retention/expiry) to apply per-environment bucket. Config template, not a
  provisioned bucket.

## ⛔ Provisioning requires explicit, resource-named approval

Nothing here creates a bucket, token, DNS record, zone, or WAF rule. Creating any
of those is an external write needing a specific approval **naming the resource
and action**. When authorized, the plan requires:

- **Separate buckets per environment** (e.g. `labconnect-artifacts-dev`,
  `-staging`).
- **Least-privilege service tokens**; encryption; object versioning/retention as
  needed; presigned URLs; CORS restrictions; malware/content checks.
- **No identifiable clinical data in R2** until data governance explicitly
  approves it.
- **DNS, custom domains, WAF, and access policies** always require a separate,
  explicit change window.

**Required from the owner before provisioning:** the account/zone, the exact
bucket names per environment, and confirmation that only synthetic/de-identified
artifacts will be stored initially.
