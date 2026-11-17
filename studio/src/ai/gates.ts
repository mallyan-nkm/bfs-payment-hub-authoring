import { checkDocument } from '../checks/checks.js';
import { modelOf } from '../core/model.js';
import { typeCheckScript } from '../core/types.js';
import { lintScript } from '../interp/sandbox.js';
import { runExamples } from '../rules/service.js';
import { missingStandardScenarios } from '../sim/runtime.js';
import { runTest } from '../testing/runner.js';
import type { StudioEnv } from '../env.js';

// The authoring gates (C16-S1, F-20). They run in order: schema, static checks, type check, tests,
// determinism. The first red gate is shown with its evidence. The same gates apply whoever wrote
// the draft, a designer or the assistant. Passing the gates never publishes: that stays with
// maker-checker.

export interface Gate { gate: string; passed: boolean; detail?: any }

export interface GateInput {
  doc: any;
  script?: { source: string; language: 'typescript' | 'python' };
  tests?: any[]; // drafted tests that travel with the draft
  tenant?: string;
}

export async function runGates(env: StudioEnv, inp: GateInput): Promise<{ gates: Gate[]; passed: boolean; firstRed?: Gate }> {
  const { doc } = inp;
  const gates: Gate[] = [];
  const issues = checkDocument(env, doc, inp.tenant);
  const schema = issues.filter((i) => i.code === 'L00');
  gates.push({ gate: 'schema', passed: schema.length === 0, detail: schema });
  const statics = issues.filter((i) => i.code !== 'L00' && i.severity === 'error');
  gates.push({ gate: 'static checks', passed: statics.length === 0, detail: statics });
  if (inp.script && doc.kind === 'activity') {
    const t = typeCheckScript(modelOf(env.store), doc, inp.script.source, inp.script.language);
    gates.push({ gate: 'type check', passed: t.length === 0, detail: t });
  }
  // Tests: a rule's examples (a passing and a failing one at least), the tests that target the
  // draft, and any drafted tests, run with the draft in place.
  const testDetail: any[] = [];
  let testsOk = true;
  if (doc.kind === 'rule' || doc.kind === 'table') {
    const ex = runExamples(doc, (n) => env.store.doc(n));
    const hasPassFail = doc.kind === 'table' || (ex.some((e) => (e.expected.findings ?? []).length === 0) && ex.some((e) => (e.expected.findings ?? []).length > 0));
    testsOk &&= ex.length > 0 && ex.every((e) => e.passed) && hasPassFail;
    testDetail.push(...ex.map((e) => ({ example: e.name, passed: e.passed, actual: e.actual })));
    if (!hasPassFail) testDetail.push({ problem: 'A rule ships at least one passing and one failing example (R-04-05)' });
  }
  if (doc.kind === 'simulator') {
    const missing = missingStandardScenarios(doc);
    testsOk &&= missing.length === 0;
    if (missing.length) testDetail.push({ problem: `Missing standard scenarios: ${missing.join(', ')}` });
  }
  const documents = doc.name ? { [doc.name]: doc, [`${doc.name}@${doc.release}`]: doc } : {};
  const tests = [...env.store.byKind('test').filter((t) => t.doc.target === doc.name).map((t) => t.doc), ...(inp.tests ?? [])];
  for (const t of tests) {
    const r = await runTest(env, t, { documents });
    testsOk &&= r.passed;
    testDetail.push({ id: t.id, passed: r.passed, failed: r.checks.filter((c) => !c.passed), error: r.error?.split('\n')[0] });
  }
  if (doc.kind === 'activity') {
    // R-03-16: at least one test per outcome.
    const covered = new Set(tests.map((t) => t.expect?.outcome));
    const uncovered = (doc.outcomes ?? []).map((o: any) => o.name).filter((o: string) => !covered.has(o));
    if (uncovered.length) {
      testsOk = false;
      testDetail.push({ problem: `No test expects: ${uncovered.join(', ')}` });
    }
  }
  gates.push({ gate: 'tests', passed: testsOk, detail: testDetail });
  if (inp.script) {
    const d = inp.script.language === 'typescript' ? lintScript(inp.script.source) : [/\bimport\s+(os|sys|time|random|socket|subprocess|datetime)\b/, /\bopen\(/].filter((re) => re.test(inp.script!.source)).map((re) => `The script uses ${re.source}, outside the injected interface`);
    gates.push({ gate: 'determinism', passed: d.length === 0, detail: d });
  }
  gates.push({ gate: 'provenance', passed: !!doc.provenance?.origin, detail: doc.provenance ?? 'No provenance: say where the draft came from (F-21)' });
  const firstRed = gates.find((g) => !g.passed);
  return { gates, passed: !firstRed, firstRed };
}
