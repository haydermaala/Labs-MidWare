# DNS before-state — spottiq.com (Hostinger)

- Captured: 2026-07-19 (read-only, no changes made)
- Source: Hostinger hPanel → Domains → spottiq.com → DNS / Nameservers
- Nameservers: `ns1.dns-parking.com`, `ns2.dns-parking.com` (Hostinger DNS)

## Records at capture time

| Type  | Name              | Priority | Content                              | TTL  |
|-------|-------------------|----------|--------------------------------------|------|
| TXT   | titan1._domainkey | 0        | `v=DKIM1; k=rsa; p=MIGf…` (Titan DKIM) | 300  |
| CNAME | www               | 0        | `www.spottiq.com.cdn.hstgr.net`      | 300  |
| A     | ftp               | 0        | `195.35.39.216`                      | 1800 |
| ALIAS | @                 | 0        | `spottiq.com.cdn.hstgr.net`          | 300  |
| TXT   | @                 | 0        | `v=spf1 include:spf.titan.email ~all` | 3600 |
| MX    | @                 | 10       | `mx1.titan.email`                    | 3600 |
| MX    | @                 | 20       | `mx2.titan.email`                    | 3600 |

## Findings

- **No `lc` record exists** — `lc.spottiq.com` is free to create with no conflict.
- Apex (`@`) and `www` serve the existing Hostinger-hosted site (`cdn.hstgr.net`); **do not touch**.
- Mail is **Titan** (Hostinger email): MX ×2 + SPF + DKIM on the apex; **do not touch**.
- **No DMARC record** exists. If transactional email uses a dedicated subdomain
  (recommended), its SPF/DKIM/DMARC records are additive on that subdomain and
  do not affect apex mail.

## Planned change (Phase H only — not yet authorized to execute)

- Add exactly one record: `CNAME lc → <railway-provided-target>` (plus any
  provider-required TXT validation record), after production service exists.

## Rollback

- Delete the added `lc` CNAME (and any added TXT validation record). No other
  record will have been modified, so no further rollback is required.
