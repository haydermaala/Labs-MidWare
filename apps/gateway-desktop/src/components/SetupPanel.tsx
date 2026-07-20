import { useState } from 'react';
import { tokens } from '@lab-connect/ui';

/**
 * First-run enrollment guidance. The desktop app is a UI, not the communication
 * process — the gateway daemon (`gatewayd`) owns enrollment and the device
 * credential. This panel collects the details an operator provides (control-plane
 * URL + a single-use bootstrap token from the web console) and produces the exact
 * `gatewayd --run` command the technician runs to enroll and begin passive
 * capture. The token is never sent anywhere by this UI.
 */

export interface EnrollInputs {
  readonly controlPlaneUrl: string;
  readonly name: string;
  readonly bootstrapToken: string;
  readonly captureAddr: string;
}

/** Build the `gatewayd --run` invocation for the given enrollment inputs. */
export function enrollCommand(inputs: EnrollInputs): string {
  const url = inputs.controlPlaneUrl.trim().replace(/\/+$/, '');
  const name = inputs.name.trim() || 'edge-gateway';
  const token = inputs.bootstrapToken.trim();
  const addr = inputs.captureAddr.trim() || '127.0.0.1:9600';
  return [
    `LC_CONTROL_PLANE_URL=${url}`,
    `GATEWAYD_NAME='${name}'`,
    `GATEWAYD_BOOTSTRAP_TOKEN=${token || '<paste-token>'}`,
    `GATEWAYD_CAPTURE_ADDR=${addr}`,
    'gatewayd --run',
  ].join(' \\\n  ');
}

export function SetupPanel(): JSX.Element {
  const [controlPlaneUrl, setUrl] = useState('https://lc.spottiq.com');
  const [name, setName] = useState('');
  const [bootstrapToken, setToken] = useState('');
  const [captureAddr, setAddr] = useState('127.0.0.1:9600');
  const [copied, setCopied] = useState(false);

  const ready = controlPlaneUrl.trim() !== '' && bootstrapToken.trim() !== '';
  const command = enrollCommand({ controlPlaneUrl, name, bootstrapToken, captureAddr });

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard may be unavailable (e.g. no permission); the text is still
      // selectable in the block below.
    }
  }

  const field: React.CSSProperties = {
    background: '#0e1420', color: tokens.color.fg, border: '1px solid #26303f',
    borderRadius: 6, padding: '8px 10px', fontSize: 13, width: '100%',
  };
  const labelText: React.CSSProperties = { fontSize: 12, color: '#9aa4b2', marginBottom: 4 };

  return (
    <details style={{ background: '#111722', border: '1px solid #26303f', borderRadius: 8, padding: tokens.space[4] }}>
      <summary style={{ cursor: 'pointer', fontWeight: 600 }}>First-time setup — enroll this gateway</summary>

      <p style={{ color: '#9aa4b2', fontSize: 13, marginTop: tokens.space[3] }}>
        Ask an operator to issue an enrollment token in the LabConnect console
        (Fleet → Add gateway). Paste it below to generate the command that enrolls
        this gateway and starts passive capture. The token is single-use and is
        never transmitted by this window.
      </p>

      <div style={{ display: 'grid', gap: tokens.space[3], marginTop: tokens.space[3] }}>
        <label>
          <div style={labelText}>Control-plane URL</div>
          <input style={field} value={controlPlaneUrl} onChange={(e) => setUrl(e.target.value)} />
        </label>
        <label>
          <div style={labelText}>Gateway name</div>
          <input style={field} value={name} placeholder="Chemistry Analyzer A" onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          <div style={labelText}>Bootstrap token (single-use)</div>
          <input style={field} value={bootstrapToken} placeholder="paste from the console" onChange={(e) => setToken(e.target.value)} />
        </label>
        <label>
          <div style={labelText}>Capture address (loopback)</div>
          <input style={field} value={captureAddr} onChange={(e) => setAddr(e.target.value)} />
        </label>
      </div>

      <div style={{ marginTop: tokens.space[4] }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: tokens.space[3], marginBottom: 6 }}>
          <span style={labelText}>Run this on the gateway host:</span>
          <button
            type="button"
            onClick={() => void copy()}
            disabled={!ready}
            style={{
              marginLeft: 'auto', background: 'transparent', color: tokens.color.accent,
              border: `1px solid ${tokens.color.accent}`, borderRadius: 6,
              padding: '2px 10px', fontSize: 12, cursor: ready ? 'pointer' : 'not-allowed',
            }}
          >
            {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
        <pre style={{
          background: '#0b0f17', color: '#c7d2e0', border: '1px solid #26303f', borderRadius: 6,
          padding: tokens.space[3], fontSize: 12, overflowX: 'auto', margin: 0, whiteSpace: 'pre',
        }}>{command}</pre>
        {!ready && (
          <p style={{ color: '#9aa4b2', fontSize: 12, marginTop: 6 }}>
            Enter the control-plane URL and a bootstrap token to complete the command.
          </p>
        )}
      </div>
    </details>
  );
}
