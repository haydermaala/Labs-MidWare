import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { describe, it, expect } from 'vitest';
import { CONTRACT_VERSION, type ResultSet } from '../src/index';

const here = dirname(fileURLToPath(import.meta.url));
const raw = readFileSync(
  resolve(here, '../../../fixtures/canonical/result_set.v0.1.json'),
  'utf8',
);

describe('canonical contract types', () => {
  it('parses the shared fixture as a ResultSet', () => {
    const rs = JSON.parse(raw) as ResultSet;
    expect(rs.results.length).toBe(3);

    const first = rs.results[0]!;
    expect(first.value.kind).toBe('numeric');
    if (first.value.kind === 'numeric') {
      // Exact decimal is a STRING preserving the trailing zero (ADR 0007).
      expect(typeof first.value.value).toBe('string');
      expect(first.value.value).toBe('5.30');
    }
    expect(first.provenance.validation).toBe('pending_review');

    // Discriminated union members are distinct and not coerced.
    expect(rs.results[1]!.value.kind).toBe('coded');
    expect(rs.results[2]!.value.kind).toBe('absent');
  });

  it('json round-trips without loss', () => {
    const rs = JSON.parse(raw) as ResultSet;
    const again = JSON.parse(JSON.stringify(rs)) as ResultSet;
    expect(again).toEqual(rs);
  });

  it('exposes the contract version', () => {
    expect(CONTRACT_VERSION).toBe('0.1.0');
  });
});
