import { tokens } from '@lab-connect/ui';

/** Connects the console to a control-plane API with an admin bearer token.
 * The token is held in memory only (never persisted). */
export function ConnectionForm({
  baseUrl,
  adminToken,
  loading,
  onBaseUrl,
  onAdminToken,
  onConnect,
}: {
  readonly baseUrl: string;
  readonly adminToken: string;
  readonly loading: boolean;
  readonly onBaseUrl: (v: string) => void;
  readonly onAdminToken: (v: string) => void;
  readonly onConnect: () => void;
}): JSX.Element {
  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        onConnect();
      }}
      style={{ display: 'flex', gap: tokens.space[3], alignItems: 'end', flexWrap: 'wrap' }}
    >
      <label style={{ display: 'grid', gap: 4, fontSize: 12, color: '#9aa4b2' }}>
        Control-plane URL
        <input
          type="url"
          value={baseUrl}
          onChange={(e) => onBaseUrl(e.target.value)}
          placeholder="https://…up.railway.app"
          style={input(320)}
        />
      </label>
      <label style={{ display: 'grid', gap: 4, fontSize: 12, color: '#9aa4b2' }}>
        Admin token
        <input
          type="password"
          value={adminToken}
          onChange={(e) => onAdminToken(e.target.value)}
          placeholder="bearer token"
          autoComplete="off"
          style={input(240)}
        />
      </label>
      <button
        type="submit"
        disabled={loading || baseUrl === '' || adminToken === ''}
        style={{
          background: tokens.color.accent,
          color: '#04121f',
          border: 'none',
          borderRadius: 6,
          padding: `${tokens.space[2]}px ${tokens.space[4]}px`,
          fontWeight: 700,
          cursor: loading ? 'wait' : 'pointer',
        }}
      >
        {loading ? 'Loading…' : 'Connect'}
      </button>
    </form>
  );
}

function input(width: number): React.CSSProperties {
  return {
    width,
    background: '#0f1319',
    color: tokens.color.fg,
    border: '1px solid #20242b',
    borderRadius: 6,
    padding: '8px 10px',
    fontSize: 13,
  };
}
