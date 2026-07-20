import { useState } from 'react';
import {
  getStatus,
  getRecentMessages,
  type CapturedMessageMeta,
  type ClientOptions,
  type GatewayStatus,
} from '@lab-connect/api-client';
import { CONTRACT_VERSION } from '@lab-connect/contracts';
import { tokens } from '@lab-connect/ui';
import { ConnectionForm } from './components/ConnectionForm';
import { StatusPanel } from './components/StatusPanel';
import { MessagesPanel } from './components/MessagesPanel';
import { SetupPanel } from './components/SetupPanel';

/**
 * Technician desktop shell. The UI is a read-only client of the local gateway
 * API; it is not the communication process. Data shown is capture-only and
 * PHI-redacted (metadata, never payloads).
 */
export function App(): JSX.Element {
  const [baseUrl, setBaseUrl] = useState('http://127.0.0.1:7373');
  const [token, setToken] = useState('');
  const [status, setStatus] = useState<GatewayStatus | null>(null);
  const [messages, setMessages] = useState<readonly CapturedMessageMeta[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  async function refresh(): Promise<void> {
    setLoading(true);
    setError(null);
    const opts: ClientOptions = { baseUrl, token };
    try {
      const [s, m] = await Promise.all([getStatus(opts), getRecentMessages(opts, 50)]);
      setStatus(s);
      setMessages(m);
    } catch (e) {
      setStatus(null);
      setError(e instanceof Error ? e.message : 'connection failed');
    } finally {
      setLoading(false);
    }
  }

  return (
    <main
      style={{
        background: tokens.color.bg,
        color: tokens.color.fg,
        minHeight: '100vh',
        padding: tokens.space[5],
        display: 'grid',
        gap: tokens.space[4],
        fontFamily: 'system-ui, sans-serif',
      }}
    >
      <header style={{ display: 'flex', alignItems: 'center', gap: tokens.space[3] }}>
        <h1 style={{ margin: 0, fontSize: 20 }}>lab-connect · technician</h1>
        <span
          style={{
            background: '#13351f',
            color: '#5fd08a',
            padding: '2px 8px',
            borderRadius: 999,
            fontSize: 12,
            fontWeight: 600,
          }}
        >
          passive capture
        </span>
        <span style={{ marginLeft: 'auto', color: '#9aa4b2', fontSize: 12 }}>
          contract v{CONTRACT_VERSION}
        </span>
      </header>

      <ConnectionForm
        baseUrl={baseUrl}
        token={token}
        loading={loading}
        onBaseUrl={setBaseUrl}
        onToken={setToken}
        onConnect={() => void refresh()}
      />

      {status === null && <SetupPanel />}

      {error && (
        <p role="alert" style={{ color: tokens.color.danger }}>
          {error}
        </p>
      )}

      {status && (
        <>
          <StatusPanel status={status} />
          <button
            type="button"
            onClick={() => void refresh()}
            disabled={loading}
            style={{
              justifySelf: 'start',
              background: 'transparent',
              color: tokens.color.accent,
              border: `1px solid ${tokens.color.accent}`,
              borderRadius: 6,
              padding: `${tokens.space[1]}px ${tokens.space[3]}px`,
              cursor: 'pointer',
            }}
          >
            Refresh
          </button>
          <MessagesPanel messages={messages} />
        </>
      )}
    </main>
  );
}
