// JSON Schema registry. Phase 1 scaffold: exposes the driver-manifest v0.1
// schema id only. Concrete schemas land alongside canonical-model in Phase 2.

export const DRIVER_MANIFEST_SCHEMA_ID =
  'https://lab-connect.invalid/schemas/driver-manifest/0.1.json' as const;

export const CANONICAL_MODEL_SCHEMA_ID =
  'https://lab-connect.invalid/schemas/canonical-model/0.1.json' as const;
