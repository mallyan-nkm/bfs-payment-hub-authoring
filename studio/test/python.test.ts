import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { runPythonScript } from '../src/interp/pysandbox.js';
import { runScript } from '../src/interp/sandbox.js';

const api = (reply: any = { status: 'accepted' }) => ({
  ports: { request: async () => reply, send: async () => ({ status: 'accepted' }) },
  rules: { run: () => ({ worst: 'pass', findings: [] }) },
  params: {}, clock: { now: () => '2026-11-17T06:00:00.000Z', today: () => '2026-11-17' }, ids: { next: () => 'id-1' }, log: { info: () => {} },
});

describe('the Python script sandbox (C08)', () => {
  it('runs a script with the injected interface', async () => {
    const r = await runPythonScript(`def run(api):\n    api.ctx['facts'] = {'x': {'n': len(api.ctx['header']['paymentId'])}}\n    return 'ok'\n`, 'a.py', { header: { paymentId: 'P-123' } }, ['header'], ['facts.x'], api() as any);
    expect(r.outcome).toBe('ok');
    expect(r.ctx.facts.x.n).toBe(5);
  });
  it('fails a read or a write outside the contract (C08-S2)', async () => {
    await expect(runPythonScript(`def run(api):\n    return api.ctx['payment']\n`, 'b.py', { header: {} }, ['header'], [], api() as any)).rejects.toThrow(/declared reads/);
    await expect(runPythonScript(`def run(api):\n    api.ctx['status'] = {'code': 'X'}\n    return 'ok'\n`, 'c.py', {}, ['header'], ['facts.x'], api() as any)).rejects.toThrow(/declared writes/);
  });
  it('has no file system, network, clock or randomness (C08-S3)', async () => {
    for (const src of [`def run(api):\n    import os\n    return 'bad'\n`, `def run(api):\n    import random\n    return 'bad'\n`, `def run(api):\n    open('/etc/hostname')\n    return 'bad'\n`, `def run(api):\n    import time\n    return 'bad'\n`]) {
      await expect(runPythonScript(src, 'd.py', {}, [], [], api() as any)).rejects.toThrow();
    }
    const r = await runPythonScript(`def run(api):\n    return 'ok' if api.clock.now() == '2026-11-17T06:00:00.000Z' else 'bad'\n`, 'e.py', {}, [], [], api() as any);
    expect(r.outcome).toBe('ok');
  });
  it('gives the same results as the TypeScript script on the parity fixtures (C08-S4)', async () => {
    const dir = path.join(__dirname, 'fixtures', 'parity');
    const cases = JSON.parse(fs.readFileSync(path.join(dir, 'cases.json'), 'utf8'));
    const py = fs.readFileSync(path.join(dir, 'p.validate.duplicate.v1.py'), 'utf8');
    const tsSrc = fs.readFileSync(path.join(__dirname, '..', 'scripts', 'p.validate.duplicate.v1.ts'), 'utf8');
    const reads = ['header.endToEndId'];
    const writes = ['facts.duplicate'];
    for (const c of cases) {
      const a = await runScript(tsSrc, 'ts', JSON.parse(JSON.stringify(c.view)), reads, writes, api(c.reply) as any);
      const b = await runPythonScript(py, 'py', JSON.parse(JSON.stringify(c.view)), reads, writes, api(c.reply) as any);
      expect(b.outcome, c.name).toBe(a.outcome);
      expect(b.changes, c.name).toEqual(a.changes);
      expect(a.outcome).toBe(c.expect.outcome);
      expect(a.ctx.facts.duplicate.found).toBe(c.expect.found);
    }
  });
});
