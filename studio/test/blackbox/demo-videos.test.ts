import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { STUDIO } from './server.js';
import { stackUp } from '../../demo/stack.js';
import { storeUp } from '../../demo/north-star.js';

// Black box, in the browser: every step of the recorded demo videos, run fast with no video and no
// voice (TH_DEMO_DRY=1). The console videos check their own results as they go: the licence is
// approved, a value out of bounds is refused, the examples turn red and green, the script is refused
// and then saved, the canvas writes the steps, the bench traces show the pay period check. So a
// change to the web app that would break a video fails here first.

const tsx = path.join(STUDIO, 'node_modules', '.bin', 'tsx');
/** Runs one production dry; asynchronous, so the test worker stays responsive while it runs. */
const dry = (which: string, limit = 300_000) => new Promise<string>((resolve) => {
  const out = fs.mkdtempSync(path.join(os.tmpdir(), `th-dry-${which}-`));
  const c = spawn(tsx, ['demo/run.ts', which, '--out', out], { cwd: STUDIO, env: { ...process.env, TH_DEMO_DRY: '1', TH_TTS: '' } });
  let o = '';
  c.stdout.on('data', (b) => (o += b));
  c.stderr.on('data', (b) => (o += b));
  const t = setTimeout(() => c.kill(), limit);
  c.on('close', () => { clearTimeout(t); resolve(o); });
});

describe('demo and training videos, dry run', () => {
  for (const which of ['concept', 'bank-configurator', 'designer']) {
    it(`${which}: every scene runs`, async () => {
      const out = await dry(which, 420_000);
      expect(out, out.slice(-3000)).toMatch(/\d+ scenes ran, dry/);
    }, 440_000);
  }
});

// The north-star video reads a store of its own (demo/north-star/store.sh up) and runs a conformance scenario on each of the four
// engines (.NET 8, Java and the Temporal test server: CLAUDE.md), so it runs only where its store is up. The engines take minutes.
describe.skipIf(!storeUp())('north-star video, dry run', () => {
  it('north-star: every scene runs', async () => {
    const out = await dry('north-star', 1_500_000);
    expect(out, out.slice(-3000)).toMatch(/\d+ scenes ran, dry/);
  }, 1_520_000);
});

// The runtime videos that drive the laptop stack (deploy/laptop) run only where a stack is up, with its secrets in the
// environment (studio/demo/README.md). The full run pays two payments on Temporal, each of which waits in real time for its
// simulator's answer, so it is given half an hour.
const up = await stackUp();
describe.skipIf(!up)('runtime videos on the laptop stack, dry run', () => {
  for (const [which, limit] of [['install', 300_000], ['full-run', 1_800_000]] as const) {
    it(`${which}: every scene runs`, async () => {
      const out = await dry(which, limit);
      expect(out, out.slice(-3000)).toMatch(/\d+ scenes ran, dry/);
    }, limit + 20_000);
  }
});
