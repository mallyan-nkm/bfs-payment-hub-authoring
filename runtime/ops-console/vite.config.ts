import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

// The operations console. Built into dist, which the console's API (runtime/src/TransactHub.Operations.Api) serves at /.
// In development, /ops/api goes to the API at TH_OPS_API (default http://localhost:7400) and /service/api to the service
// console's API at TH_SERVICE_API (default http://localhost:7410).
const here = fileURLToPath(new URL('.', import.meta.url));

export default defineConfig({
  root: here,
  plugins: [react()],
  build: { outDir: 'dist', emptyOutDir: true },
  server: {
    port: 5174,
    proxy: {
      '/ops/api': { target: process.env.TH_OPS_API ?? 'http://localhost:7400', changeOrigin: true },
      '/service/api': { target: process.env.TH_SERVICE_API ?? 'http://localhost:7410', changeOrigin: true },
    },
  },
});
