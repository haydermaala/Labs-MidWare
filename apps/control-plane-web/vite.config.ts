import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Web control-plane build config.
//
// In development the API is proxied under the same origin as the app, which
// mirrors the launch topology (console and API served together at
// lc.spottiq.com) and means no CORS allowlist entry is needed for local work.
// Point the proxy at another environment with LC_API_TARGET.
declare const process: { env: Record<string, string | undefined> };
const apiTarget = process.env['LC_API_TARGET'] ?? 'https://labs-midware-staging.up.railway.app';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: true },
      '/health': { target: apiTarget, changeOrigin: true, secure: true },
    },
  },
  build: { outDir: 'dist', sourcemap: true },
});
