import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import Ajv2020 from 'ajv/dist/2020.js';
import { describe, it, expect } from 'vitest';

const here = dirname(fileURLToPath(import.meta.url));
const schema = JSON.parse(
  readFileSync(resolve(here, '../schemas/result-set.v0.1.schema.json'), 'utf8'),
);
const fixture = JSON.parse(
  readFileSync(resolve(here, '../../../fixtures/canonical/result_set.v0.1.json'), 'utf8'),
);

const ajv = new Ajv2020({ allErrors: true, strict: true });
const validate = ajv.compile(schema);

describe('ResultSet JSON Schema v0.1', () => {
  it('accepts the shared canonical fixture', () => {
    const ok = validate(fixture);
    expect(validate.errors ?? []).toEqual([]);
    expect(ok).toBe(true);
  });

  it('rejects a numeric value expressed as a JSON number (decimals must be strings)', () => {
    const bad = structuredClone(fixture);
    bad.results[0].value = { kind: 'numeric', value: 5.3 };
    expect(validate(bad)).toBe(false);
  });

  it('rejects an unknown result status', () => {
    const bad = structuredClone(fixture);
    bad.results[0].status = 'made_up';
    expect(validate(bad)).toBe(false);
  });

  it('rejects unexpected additional properties', () => {
    const bad = structuredClone(fixture);
    bad.results[0].surprise = true;
    expect(validate(bad)).toBe(false);
  });

  it('rejects a result missing its provenance', () => {
    const bad = structuredClone(fixture);
    delete bad.results[0].provenance;
    expect(validate(bad)).toBe(false);
  });
});
