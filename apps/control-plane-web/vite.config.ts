import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Web control plane build config. Phase 1: static shell only.
export default defineConfig({
  plugins: [react()],
  server: { port: 5173, strictPort: true },
  build: { outDir: 'dist', sourcemap: true },
});
