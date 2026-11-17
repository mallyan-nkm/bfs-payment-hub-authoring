import { spawn } from 'node:child_process';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { coveredByAny, diff } from '../core/paths.js';
import { ContractViolation, ScriptError, type ScriptApi, type ScriptResult } from './sandbox.js';

// The Python script sandbox (C08). One isolated interpreter process per call (`python3 -I`),
// talking JSON lines; port and rule calls come back to the studio, so a Python script runs on
// the same simulators, rule service and virtual clock as a TypeScript one.

const HARNESS = process.env.TH_PY_HARNESS ?? path.join(path.dirname(fileURLToPath(typeof __filename !== 'undefined' ? 'file://' + __filename : import.meta.url)), 'py_harness.py');
export const PYTHON = process.env.TH_PYTHON ?? 'python3';

export async function runPythonScript(
  source: string,
  file: string,
  view: any,
  reads: string[],
  writes: string[],
  api: Omit<ScriptApi, 'ctx'>,
  timeoutMs = 5000,
): Promise<ScriptResult> {
  const before = JSON.parse(JSON.stringify(view));
  const roots = [...new Set([...reads, ...writes].map((r) => r.replace('[]', '').split('.')[0]))];
  const child = spawn(PYTHON, ['-I', '-S', HARNESS], { stdio: ['pipe', 'pipe', 'pipe'], env: {} });
  let buf = '';
  let stderr = '';
  child.stderr.on('data', (d) => (stderr += d));
  const timer = setTimeout(() => child.kill('SIGKILL'), timeoutMs);
  const result = await new Promise<any>((resolve, reject) => {
    child.on('error', reject);
    child.on('exit', (code) => {
      if (code !== 0) reject(new ScriptError('ScriptError', `The Python script ended with code ${code}: ${stderr.trim().split('\n').slice(-1)[0] ?? ''}`));
    });
    child.stdout.on('data', async (d) => {
      buf += d;
      let i;
      while ((i = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, i);
        buf = buf.slice(i + 1);
        const msg = JSON.parse(line);
        if (msg.result || msg.failure) return resolve(msg);
        try {
          let value: any;
          if (msg.call === 'request') value = await api.ports.request(msg.port);
          else if (msg.call === 'send') value = await api.ports.send(msg.port);
          else if (msg.call === 'rules') value = api.rules.run(msg.name, msg.facts ?? undefined);
          child.stdin.write(JSON.stringify({ value }) + '\n');
        } catch (e: any) {
          child.stdin.write(JSON.stringify({ error: String(e?.message ?? e) }) + '\n');
        }
      }
    });
    child.stdin.write(JSON.stringify({ source, file, view, roots, params: api.params, now: api.clock.now() }) + '\n');
  }).finally(() => {
    clearTimeout(timer);
    child.stdin.end();
  });
  if (result.failure) {
    if (result.failure.code === 'R-03-07') throw new ContractViolation('R-03-07', result.failure.message);
    throw new ScriptError('ScriptError', result.failure.message);
  }
  const out = result.result.ctx;
  const changes = diff(before, out);
  for (const c of changes) {
    const p = c.path.replace(/\[\d+\]/g, '[]');
    if (!coveredByAny(writes, p)) throw new ContractViolation('R-03-07', `The script wrote ${c.path}, which is not in its declared writes`);
  }
  return { outcome: result.result.outcome, reasonCode: result.result.reasonCode ?? undefined, changes, ctx: out, logs: result.result.logs ?? [] };
}
