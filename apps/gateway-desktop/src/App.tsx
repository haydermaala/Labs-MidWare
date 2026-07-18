import { CONTRACT_VERSION } from '@lab-connect/contracts';
import { tokens } from '@lab-connect/ui';

// Phase 1 technician shell. The desktop app is a UI only; the gateway daemon
// (gatewayd) is the communication process and runs independently.
export function App(): JSX.Element {
  return (
    <main style={{ background: tokens.color.bg, color: tokens.color.fg, padding: tokens.space[5] }}>
      <h1>lab-connect · technician</h1>
      <p>Phase 1 scaffold. Contract v{CONTRACT_VERSION}.</p>
      <p>Mode: passive capture (default).</p>
    </main>
  );
}
