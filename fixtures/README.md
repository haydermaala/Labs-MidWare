# Fixtures — SYNTHETIC / DE-IDENTIFIED ONLY

Every file in this directory is synthetic or irreversibly de-identified. **No
real patient data, no confidential vendor material, ever.** CI enforces this
policy area and secret scanning runs on all paths.

Each fixture, when added, must carry:

- source context (which synthetic scenario it represents),
- protocol/driver version,
- expected parse output,
- expected normalized canonical object,
- expected validation/delivery decision.

Fixtures are populated starting in Phase 2 (contracts) and Phase 4 (ASTM).
