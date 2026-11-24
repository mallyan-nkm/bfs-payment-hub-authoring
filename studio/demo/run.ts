import * as fs from 'node:fs';
import * as path from 'node:path';
import { STUDIO } from '../test/blackbox/server.js';
// A key of its own for the run's studio when none is set: approving a change set signs what it builds (R-09-23), as in the tests.
import '../test/support/signing-env.js';
import { record, type Production } from './record.js';

// Records one production: npx tsx demo/run.ts concept|north-star|bank-configurator|designer|install|end-to-end|service-console [--out dir]
// (A separate entry point: the productions import record.ts, so record.ts must not wait on them.)

const PRODUCTIONS: Record<string, () => Promise<Production>> = {
  // The concept: the ideas drawn as a kitchen, then the same ideas in the studio (it replaces the earlier ideas and concepts videos).
  concept: () => import('./concept.js') as any,
  // The north star: the architecture diagram, then each tenet with the studio, the store's tables and the four engines (it replaces
  // the drawn runtime video 2).
  'north-star': () => import('./north-star.js') as any,
  // One video per console (5a, 5b; the service console's is recorded on the laptop stack): the studio as a bank's configurator
  // and approver, and the studio as the platform's designer and developer. They replace the build-a-scheme and hands-on videos.
  'bank-configurator': () => import('./bank-configurator.js') as any,
  designer: () => import('./designer.js') as any,
  // The laptop stack videos (docs/demo/): the installation, and the end to end.
  install: () => import('./install.js') as any,
  // Video 4, end to end on the laptop stack: the test bench, each step, two failures, a rule change and a new hook.
  'end-to-end': () => import('./end-to-end.js') as any,
  // Video 5c, the service console on the laptop stack, page by page, with a parked step diagnosed and retried.
  'service-console': () => import('./service-console.js') as any,
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
