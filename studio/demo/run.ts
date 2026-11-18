import * as fs from 'node:fs';
import * as path from 'node:path';
import { STUDIO } from '../test/blackbox/server.js';
import { record, type Production } from './record.js';

// Records one production: npx tsx demo/run.ts concepts|build|ideas|hands-on [--out dir]
// (A separate entry point: the productions import record.ts, so record.ts must not wait on them.)

const PRODUCTIONS: Record<string, () => Promise<Production>> = {
  concepts: () => import('./concepts.js') as any,
  build: () => import('./build.js') as any,
  ideas: () => import('./ideas.js') as any,
  'hands-on': () => import('./hands-on.js') as any,
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
