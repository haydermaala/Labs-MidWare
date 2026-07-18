# Cross-cutting test suites

- `conformance/` — protocol conformance / golden vectors (Phase 4+).
- `integration/` — transport + persistence + API/database (Phase 3+).
- `security/` — authz, tenant isolation, SSRF/path traversal, package tampering,
  secret leakage, resource exhaustion (Phase 8+).
- `end-to-end/` — simulator → gateway → mock LIS → control plane (Phase 5+).

Unit tests live next to the code they cover (Rust `#[cfg(test)]`, xUnit projects).
