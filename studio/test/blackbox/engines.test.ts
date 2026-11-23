import { spawnSync } from 'node:child_process';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { STUDIO } from './server.js';

// Black box, opt in (TH_ENGINES=1): the conformance kit on the real engines. DTFx and Elsa need dotnet-sdk-8.0;
// Flowable needs Java and `engines/flowable/setup.sh` to have fetched its jars.

const on = process.env.TH_ENGINES === '1';
const run = (script: string) => spawnSync(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), [script], { cwd: STUDIO, encoding: 'utf8', timeout: 1500000 });

describe.skipIf(!on)('the conformance kit on real engines', () => {
  it('passes every scenario on DTFx', () => {
    const r = run('engines/dtfx/run-kit.ts');
    expect(r.stdout).toMatch(/(\d+) of \1 conformance scenarios pass on DTFx/);
  }, 1500000);

  it('passes every scenario on Elsa', () => {
    const r = run('engines/elsa/run-kit.ts');
    expect(r.stdout).toMatch(/(\d+) of \1 conformance scenarios pass on Elsa/);
  }, 1500000);

  // The Flowable binding applies R-05-43 to R-05-45 (docs/ISSUES.md I-49).
  it('passes every scenario on Flowable', () => {
    const r = run('engines/flowable/run-kit.ts');
    expect(r.stdout).toMatch(/(\d+) of \1 conformance scenarios pass on Flowable/);
  }, 1500000);
});
