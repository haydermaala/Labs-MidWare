// JSON Schema registry. Schemas themselves live under `schemas/` as the
// authoritative contract; this module exposes their canonical `$id`s.

export const DRIVER_MANIFEST_SCHEMA_ID =
  'https://lab-connect.invalid/schemas/driver-manifest/0.1.json' as const;

export const CANONICAL_MODEL_SCHEMA_ID =
  'https://lab-connect.invalid/schemas/canonical-model/0.1.json' as const;

/** `$id` of the canonical ResultSet schema (`schemas/result-set.v0.1.schema.json`). */
export const RESULT_SET_SCHEMA_ID =
  'https://lab-connect.invalid/schemas/canonical-model/0.1/result-set.json' as const;
