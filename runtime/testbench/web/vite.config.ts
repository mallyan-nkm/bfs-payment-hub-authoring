import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The test bench's web UI. Built into web/dist, which the test bench service serves at /.
export default defineConfig({
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    port: 5183,
    proxy: Object.fromEntries(['/api', '/auth', '/dev-idp'].map((p) => [p, { target: `http://127.0.0.1:${process.env.TH_TESTBENCH_PORT ?? 4380}`, changeOrigin: false }])),
  },
});
