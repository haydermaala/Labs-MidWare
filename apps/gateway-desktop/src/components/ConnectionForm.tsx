import { tokens } from '@lab-connect/ui';

/** First-run/connection form: gateway API base URL + bearer token. */
export function ConnectionForm({
  baseUrl,
  token,
  loading,
  onBaseUrl,
  onToken,
  onConnect,
}: {
  baseUrl: string;
  token: string;
  loading: boolean;
  onBaseUrl: (v: string) => void;
  onToken: (v: string) => void;
  onConnect: () => void;
}): JSX.Element {
  const input: React.CSSProperties = {
    background: '#0f131a',
    color: tokens.color.fg,
    border: '1px solid #2a2f3a',
    borderRadius: 6,
    padding: tokens.space[2],
    fontSize: 13,
  };
  return (
    <form
      aria-label="Connect to gateway"
      onSubmit={(e) => {
        e.preventDefault();
        onConnect();
      }}
      style={{ display: 'grid', gap: tokens.space[2], maxWidth: 460 }}
    >
      <label style={{ display: 'grid', gap: 4, fontSize: 13 }}>
        Gateway API URL
        <input
          style={input}
          value={baseUrl}
          onChange={(e) => onBaseUrl(e.target.value)}
          placeholder="http://127.0.0.1:7373"
        />
      </label>
      <label style={{ display: 'grid', gap: 4, fontSize: 13 }}>
        Bearer token
        <input
          style={input}
          type="password"
          value={token}
          onChange={(e) => onToken(e.target.value)}
          placeholder="GATEWAYD_API_TOKEN"
        />
      </label>
      <button
        type="submit"
        disabled={loading}
        style={{
          background: tokens.color.accent,
          color: '#fff',
          border: 'none',
          borderRadius: 6,
          padding: `${tokens.space[2]}px ${tokens.space[4]}px`,
          fontWeight: 600,
          cursor: loading ? 'default' : 'pointer',
          opacity: loading ? 0.6 : 1,
          justifySelf: 'start',
        }}
      >
        {loading ? 'Connecting…' : 'Connect'}
      </button>
    </form>
  );
}
