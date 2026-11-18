import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { STUDIO } from './server.js';

// Black box, in the browser: every step of the recorded demo and training videos, run fast with no
// video and no voice (TH_DEMO_DRY=1). The hands-on video checks its own results as it goes: the
// examples turn red and green, the canvas writes the steps, the bench traces show the month check
// and the bank's hold. So a change to the web app that would break a video fails here first.

const tsx = path.join(STUDIO, 'node_modules', '.bin', 'tsx');
/** Runs one production dry; asynchronous, so the test worker stays responsive while it runs. */
const dry = (which: string) => new Promise<string>((resolve) => {
  const out = fs.mkdtempSync(path.join(os.tmpdir(), `th-dry-${which}-`));
  const c = spawn(tsx, ['demo/run.ts', which, '--out', out], { cwd: STUDIO, env: { ...process.env, TH_DEMO_DRY: '1', TH_TTS: '' } });
  let o = '';
  c.stdout.on('data', (b) => (o += b));
  c.stderr.on('data', (b) => (o += b));
  const t = setTimeout(() => c.kill(), 300_000);
  c.on('close', () => { clearTimeout(t); resolve(o); });
});

describe('demo and training videos, dry run', () => {
  for (const which of ['concepts', 'build', 'ideas', 'hands-on']) {
    it(`${which}: every scene runs`, async () => {
      const out = await dry(which);
      expect(out, out.slice(-3000)).toMatch(/\d+ scenes ran, dry/);
    }, 320_000);
  }
});
