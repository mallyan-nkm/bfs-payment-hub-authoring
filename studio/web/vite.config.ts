import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

// The studio web app. Built into web/dist, which the studio server serves at /.
const here = fileURLToPath(new URL('.', import.meta.url));

export default defineConfig({
  root: here,
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    port: 5173,
    proxy: { '/api': { target: `http://localhost:${process.env.STUDIO_PORT ?? 4317}`, changeOrigin: true } },
  },
});
