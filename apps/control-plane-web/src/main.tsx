import React from 'react';
import { createRoot } from 'react-dom/client';
import { uiCss } from '@lab-connect/ui';
import { App } from './App';
import { shellCss } from './shell/AppShell';
import { tenantSwitcherCss } from './shell/TenantSwitcher';

// Design-system styles are injected once at startup (tokens, base reset, focus
// ring, component states, responsive shell rules).
const style = document.createElement('style');
style.textContent = `${uiCss}${shellCss}${tenantSwitcherCss}
.lc-sr-only { position:absolute; width:1px; height:1px; padding:0; margin:-1px; overflow:hidden; clip:rect(0 0 0 0); white-space:nowrap; border:0; }
a { color: inherit; }
`;
document.head.appendChild(style);

const el = document.getElementById('root');
if (!el) {
  throw new Error('root element missing');
}
createRoot(el).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
