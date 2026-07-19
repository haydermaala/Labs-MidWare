import { render } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import type { CapturedMessageMeta, GatewayStatus } from '@lab-connect/api-client';
import { App } from '../src/App';
import { StatusPanel } from '../src/components/StatusPanel';
import { MessagesPanel } from '../src/components/MessagesPanel';

describe('technician UI', () => {
  it('App shows the passive-capture badge and title', () => {
    const { container } = render(<App />);
    expect(container.textContent).toContain('passive capture');
    expect(container.textContent).toContain('technician');
  });

  it('StatusPanel renders mode, schema, and outbox counts', () => {
    const status: GatewayStatus = {
      service: 'gatewayd',
      version: '0.1.0',
      mode: 'passive_capture',
      schema_version: 1,
      outbox: { pending: 2, delivered: 1, dead: 0 },
      audit_events: 5,
    };
    const { container } = render(<StatusPanel status={status} />);
    expect(container.textContent).toContain('passive_capture');
    expect(container.textContent).toContain('Pending');
    expect(container.textContent).toContain('v1');
  });

  it('MessagesPanel is capture-only: metadata shown, payload never', () => {
    const messages: CapturedMessageMeta[] = [
      {
        id: '3f2504e0-4f89-41d3-9a0c-0305e82c3301',
        transport: 'astm',
        received_at: '2026-07-18T10:00:00Z',
        byte_len: 107,
      },
    ];
    const { container } = render(<MessagesPanel messages={messages} />);
    expect(container.textContent).toContain('capture-only, payloads redacted');
    expect(container.textContent).toContain('astm');
    expect(container.textContent).toContain('107');
    // Only a truncated id is shown; no raw ASTM payload content leaks through.
    expect(container.textContent).toContain('3f2504e0');
    expect(container.textContent).not.toContain('H|');
    expect(container.textContent).not.toContain('GLU');
  });

  it('MessagesPanel shows an empty state', () => {
    const { container } = render(<MessagesPanel messages={[]} />);
    expect(container.textContent).toContain('No messages captured yet');
  });
});
