import * as fs from 'node:fs';
import * as path from 'node:path';
import { STUDIO } from '../test/blackbox/server.js';
// A key of its own for the run's studio when none is set: approving a change set signs what it builds (R-09-23), as in the tests.
import '../test/support/signing-env.js';
import { record, type Production } from './record.js';

// Records one production: npx tsx demo/run.ts concept|north-star|build|hands-on|install|full-run [--out dir]
// (A separate entry point: the productions import record.ts, so record.ts must not wait on them.)

const PRODUCTIONS: Record<string, () => Promise<Production>> = {
  // The concept: the ideas drawn as a kitchen, then the same ideas in the studio (it replaces the earlier ideas and concepts videos).
  concept: () => import('./concept.js') as any,
  // The north star: the architecture diagram, then each tenet with the studio, the store's tables and the four engines (it replaces
  // the drawn runtime video 2).
  'north-star': () => import('./north-star.js') as any,
  build: () => import('./build.js') as any,
  'hands-on': () => import('./hands-on.js') as any,
  // The runtime videos (docs/demo/runtime/): the laptop stack and its consoles, the full run.
  install: () => import('./install.js') as any,
  'full-run': () => import('./full-run.js') as any,
};
const which = process.argv[2];
if (!which || !PRODUCTIONS[which]) {
  console.error(`Usage: npx tsx demo/run.ts ${Object.keys(PRODUCTIONS).join('|')} [--out dir]`);
  process.exit(2);
}
const outArg = process.argv.indexOf('--out');
const outDir = path.resolve(outArg > 0 ? process.argv[outArg + 1] : path.join(STUDIO, '..', 'docs', 'demo'));
fs.mkdirSync(outDir, { recursive: true });
await record(await PRODUCTIONS[which](), outDir);
