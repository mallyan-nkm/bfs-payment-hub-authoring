import { canonicalJson, clone, contentHash } from '../core/canonical.js';
import { toYaml } from '../core/yaml.js';
import { Checker, pinnable, type Issue } from '../checks/checks.js';
import { generateBpmn, portabilityReport } from './bpmn.js';
import { expandWorkflow } from '../core/expand.js';
import { zip } from './zip.js';
import type { StudioEnv } from '../env.js';

// Export (Part 9; C05, C20, C21, C22). A package holds the workflow, leg, segment and handler
// documents with their `artefacts` pins, the activity contracts, rules and maps, a manifest, the
// engine binding, the test pack and the conformance pack (R-09-01). Temporal and DTFx run the
// documents on an interpreter; Flowable gets generated BPMN. The same input gives the same bytes.

export type Target = 'temporal' | 'dtfx' | 'flowable';

export interface ExportRequest {
  workflows: string[];
  target: Target;
  tenant?: string;
  includeTests?: boolean;
}

export interface ExportPackage {
  manifest: any;
  files: Record<string, string>;
  issues: Issue[];
}

const KIND_DIR: Record<string, string> = { activity: 'activities', rule: 'rules', ruleset: 'rules', table: 'rules', workflow: 'workflows', port: 'ports', extension: 'extensions', hook: 'hooks' };

function scopeOf(name: string): string {
  const parts = name.split('.');
  return parts[0] === 'p' || parts[0] === 'b' ? parts[0] : parts.slice(0, 2).join('.');
}

/** The artefacts block of a workflow or hook: exact release and hash of everything it uses (R-01-14). */
export function artefactsBlock(env: StudioEnv, checker: Checker, doc: any) {
  return pinnable(checker, doc).map((n) => {
    const e = env.store.latest(n)!;
    return { name: n, kind: e.kind, release: e.release, hash: e.hash };
  });
}

export function exportPackage(env: StudioEnv, req: ExportRequest): ExportPackage {
  const checker = Checker.fromEnv(env);
  const files: Record<string, string> = {};
  const issues: Issue[] = [];
  const binding = env.store.byKind('engine-binding').map((e) => e.doc).find((b) => b.target === req.target);
  if (!binding) throw new Error(`No engine binding for ${req.target}`);

  // The flows: chosen workflows, the legs that fill their slots, and the handlers of those schemes.
  const flows = new Map<string, any>();
  const addFlow = (n: string) => {
    if (flows.has(n)) return;
    const e = env.store.latest(n);
    if (!e) throw new Error(`${n} does not resolve`);
    flows.set(n, e.doc);
    for (const st of walkUse(e.doc)) addFlow(st);
  };
  for (const w of req.workflows) addFlow(w);
  for (const scheme of env.store.byKind('scheme').map((e) => e.doc)) {
    const legs = (scheme.legs ?? []).filter((l: string) => {
      const d = env.store.doc(l);
      return d && (d.implements?.of ?? []).some((o: string) => flows.has(o));
    });
    if (!legs.length) continue;
    for (const l of legs) addFlow(l);
    for (const h of scheme.handlers ?? []) addFlow(h);
    files[`schemes/${scheme.scope}.yaml`] = toYaml(scheme);
  }

  // Hooks a tenant binds, with its configuration set (R-09-02, R-06-18).
  const config = req.tenant ? env.store.configurationSet(req.tenant) : undefined;
  if (config) {
    for (const b of config.bindings ?? []) {
      const h = env.store.doc(b.hook, b.release);
      if (h) flows.set(h.name, h);
    }
    files[`configuration/${req.tenant}.yaml`] = toYaml(config);
  }

  const blocks = new Map<string, any>();
  const tasks: any[] = [];
  const flowEntries: any[] = [];
  for (const [n, d] of [...flows].sort(([a], [b]) => a.localeCompare(b))) {
    // An engine receives the expanded form only, with the authored release and hash (R-09-17, R-05-36).
    const out = clone(expandWorkflow(d, (x) => env.store.doc(x)));
    out.artefacts = artefactsBlock(env, checker, d);
    for (const a of out.artefacts) blocks.set(a.name, env.store.doc(a.name, a.release));
    // L23: the pins are complete and exact.
    issues.push(...new Checker([...checker.docs], []).check(out).filter((i) => i.code === 'L23'));
    files[`${KIND_DIR[out.kind] ?? 'workflows'}/${n}@${out.release}.yaml`] = toYaml(out);
    flowEntries.push({ name: n, kind: out.kind, wfKind: out.wfKind, release: out.release, hash: contentHash(d), form: out.form ?? 'authored', pins: out.artefacts.length });
    if (out.kind === 'workflow' && out.wfKind === 'segment') continue;
    if (req.target === 'flowable' && out.kind === 'workflow' && out.wfKind !== 'handler') {
      files[`bpmn/${n}@${out.release}.bpmn20.xml`] = generateBpmn(out, { queueOf: (name) => queueOf(binding, name), resolve: (x) => env.store.doc(x) });
    }
    if (out.kind === 'workflow' && out.wfKind === 'handler') {
      // Handlers run as plain request handlers at the interop boundary, outside the engine (R-09-14).
      tasks.push({ kind: 'handler', task: `${n}@${out.release}`, endpoint: 'interop' });
    } else {
      tasks.push({ kind: out.kind === 'hook' ? 'hook' : out.wfKind === 'leg' ? 'child-workflow' : 'workflow', task: `${n}@${out.release}`, queue: queueOf(binding, n) });
    }
  }
  // Blocks and what they pull in: ports, compensations, rule sets' rules.
  for (const [n, d] of [...blocks]) {
    for (const p of d?.ports ?? []) if (!blocks.has(p)) blocks.set(p, env.store.doc(p));
    if (d?.compensation && !blocks.has(d.compensation)) blocks.set(d.compensation, env.store.doc(d.compensation));
    void n;
  }
  for (const [n, d] of [...blocks].sort(([a], [b]) => a.localeCompare(b))) {
    if (!d) continue;
    files[`${KIND_DIR[d.kind] ?? d.kind}/${n}@${d.release}.yaml`] = toYaml(d);
    if (d.kind === 'activity') tasks.push({ kind: 'activity', task: `${n}@${d.release}`, queue: queueOf(binding, n), binding: d.implementation?.binding });
  }
  for (const ext of env.store.byKind('extension')) {
    if ([...flows.keys(), ...blocks.keys()].some((n) => scopeOf(n) === ext.scope)) files[`extensions/${ext.name}@${ext.release}.yaml`] = toYaml(ext.doc);
  }

  files['binding.yaml'] = toYaml(binding);
  if (req.includeTests !== false) {
    for (const t of env.store.byKind('test')) {
      if (flows.has(t.doc.target) || blocks.has(t.doc.target)) files[`tests/${t.name}.yaml`] = toYaml(t.doc);
    }
  }
  const kit = env.store.all().find((e) => e.kind === 'conformance-kit');
  if (kit) files['conformance/kit.yaml'] = toYaml(kit.doc);
  const report = portabilityReport([...flows.values()], req.target);
  files['portability.json'] = JSON.stringify(report, null, 2) + '\n';

  const manifest = {
    kind: 'export',
    target: req.target,
    strategy: binding.strategy,
    dsl: '1.0.0',
    taskNaming: binding.taskNaming,
    tenant: req.tenant,
    containsSimulators: false,
    promotable: issues.filter((i) => i.severity === 'error').length === 0,
    flows: flowEntries,
    tasks: tasks.sort((a, b) => a.task.localeCompare(b.task)),
    files: Object.keys(files).sort().map((f) => ({ path: f, hash: 'sha256:' + sha(files[f]) })),
  };
  files['manifest.yaml'] = toYaml(manifest);
  files['README.md'] = readme(manifest);
  return { manifest, files, issues };
}

function walkUse(d: any): string[] {
  const out: string[] = [];
  const visit = (steps: any[]) => {
    for (const st of steps ?? []) {
      if (st.use) out.push(st.use);
      for (const a of Object.values(st.handle ?? {})) if (Array.isArray(a)) visit(a);
      if (st.try) visit(st.try);
      if (st.parallel) for (const b of st.parallel.branches ?? []) visit(b);
    }
  };
  visit(d.do);
  return out;
}

function queueOf(binding: any, name: string): string {
  return binding.queues?.[scopeOf(name)] ?? binding.queues?.p ?? 'default';
}

import { createHash } from 'node:crypto';
function sha(s: string) {
  return createHash('sha256').update(s, 'utf8').digest('hex');
}

function readme(m: any): string {
  return [
    `# Export package for ${m.target}`,
    '',
    `Strategy: ${m.strategy}. DSL ${m.dsl}. Tasks are registered as \`${m.taskNaming}\`, so two releases of one line run side by side.`,
    '',
    m.strategy === 'interpreter'
      ? 'The workflow documents are data. One generic interpreter workflow reads them; each activity is a task on the queue the binding derives from its scope.'
      : 'Each workflow is generated BPMN 2.0. Each activity is an external worker task on the topic the binding derives from its scope.',
    '',
    'Handlers are plain request handlers at the interop boundary, not engine workflows.',
    '',
    `Flows: ${m.flows.length}. Tasks: ${m.tasks.length}. Files: ${m.files.length}.`,
    '',
    'The conformance kit in `conformance/kit.yaml` must be green on this target before release (R-09-08).',
    '',
  ].join('\n');
}

export function exportZip(pkg: ExportPackage): Buffer {
  return zip(Object.entries(pkg.files).sort(([a], [b]) => a.localeCompare(b)).map(([name, content]) => ({ name, content: Buffer.from(content, 'utf8') })));
}

export function packageHash(pkg: ExportPackage): string {
  return 'sha256:' + sha(canonicalJson(pkg.files));
}
