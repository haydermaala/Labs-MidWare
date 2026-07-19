import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Test config for the technician UI (component tests run in jsdom).
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    include: ['test/**/*.test.tsx'],
  },
});
