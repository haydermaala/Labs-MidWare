## What & why

<!-- Scope this PR to one concern. Do not mix unrelated refactors with features. -->

## Checklist

- [ ] `scripts/verify.sh` passes locally (rust + dotnet + js)
- [ ] Conventional Commit title
- [ ] No secrets, real patient data, or confidential vendor material added
- [ ] Parser/untrusted-input changes include property/fuzz coverage
- [ ] ADR added/updated if this changes architecture or the fixed stack
- [ ] Docs updated (`docs/`, `README`, `CHANGELOG`) where relevant

## Safety

- [ ] No external-account or production changes (or: named approval linked below)
- [ ] No new outbound-to-device capability (or: separately approved + allowlisted)
