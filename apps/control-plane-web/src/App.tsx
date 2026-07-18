import { CONTRACT_VERSION } from '@lab-connect/contracts';
import { tokens } from '@lab-connect/ui';

// Phase 1 shell: proves the app builds and consumes shared packages.
// No fleet/driver/mapping/clinical features yet (Phase 8).
export function App(): JSX.Element {
  return (
    <main style={{ background: tokens.color.bg, color: tokens.color.fg, padding: tokens.space[5] }}>
      <h1>lab-connect · control plane</h1>
      <p>Phase 1 scaffold. Contract v{CONTRACT_VERSION}.</p>
    </main>
  );
}
