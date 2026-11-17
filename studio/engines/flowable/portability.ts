// The portability report for Flowable (C22-S5): every factory workflow is reported as portable,
// or with the exact step that is not. Each workflow is expanded (R-09-17), generated, and the
// BPMN is deployed into a real Flowable engine.
//   npx tsx engines/flowable/portability.ts
import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { callsComposite, expandWorkflow } from '../../src/core/expand.js';
import { flowablePortable, generateBpmn, portabilityReport, safe } from '../../src/export/bpmn.js';
import { openEnv } from '../../src/env.js';
import { FLOWABLE_DIR, buildHarness } from './adapter.js';

const env = openEnv({ dataRoot: '' });
buildHarness();
const dir = path.join(FLOWABLE_DIR, 'out', 'portability');
fs.rmSync(dir, { recursive: true, force: true });
fs.mkdirSync(dir, { recursive: true });
const resolve = (n: string) => env.store.doc(n);
const rows: { flow: string; file?: string; refused?: string; items: any[] }[] = [];
for (const e of env.store.byKind('workflow')) {
  const d = e.doc;
  if (d.wfKind === 'segment' || d.wfKind === 'subflow' || d.wfKind === 'handler') continue;
  const wf = callsComposite(d, resolve) ? expandWorkflow(d, resolve) : d;
  const row: (typeof rows)[number] = { flow: `${d.name}@${d.release}`, items: flowablePortable(wf).steps };
  try {
    const file = path.join(dir, `${safe(row.flow)}.bpmn20.xml`);
    fs.writeFileSync(file, generateBpmn(wf, { queueOf: (n) => `th-${n.split('.').slice(0, 2).join('-')}`, resolve }));
    row.file = file;
  } catch (x: any) {
    row.refused = String(x?.message ?? x);
  }
  rows.push(row);
}
const files = rows.filter((r) => r.file).map((r) => r.file!);
const cp = [path.join(FLOWABLE_DIR, 'harness', 'target', 'classes'), path.join(FLOWABLE_DIR, 'harness', 'target', 'lib', '*')].join(path.delimiter);
const out = execFileSync('java', ['-cp', cp, 'th.flowable.Harness', '--validate', ...files], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
for (const line of out.split('\n')) {
  const m = /^REFUSED (\S+) (.*)$/.exec(line);
  if (m) { const r = rows.find((x) => x.file === m[1]); if (r) r.refused = `Flowable refused the BPMN: ${m[2]}`; }
}
let portable = 0;
for (const r of rows.sort((a, b) => a.flow.localeCompare(b.flow))) {
  const ok = !r.refused && r.items.length === 0;
  if (ok) portable++;
  console.log(`${ok ? 'PORTABLE    ' : 'NOT PORTABLE'}  ${r.flow}`);
  if (r.refused) console.log(`      ${r.refused}`);
  for (const i of r.items) console.log(`      step ${i.step ?? '?'}: ${i.note}${i.alternative ? ` Alternative: ${i.alternative}` : ''}`);
  const warn = portabilityReport([env.store.doc(r.flow.split('@')[0])], 'flowable').filter((i) => i.severity === 'warning');
  for (const w of warn) console.log(`      warning${w.step ? ` (step ${w.step})` : ''}: ${w.note}`);
}
console.log(`${portable} of ${rows.length} factory workflows are portable to Flowable; each one deployed into Flowable unless shown`);
