import { canonicalJson, clone, contentHash } from '../core/canonical.js';
import { toYaml } from '../core/yaml.js';
import { Checker, pinnable, type Issue } from '../checks/checks.js';
import { generateBpmn, portabilityReport } from './bpmn.js';
import { expandWorkflow } from '../core/expand.js';
import { zip } from './zip.js';
import { startEntryFor } from '../testing/runner.js';
import { queueName } from './bindings.js';
import type { StudioEnv } from '../env.js';

// Export (Part 9; C05, C20, C21, C22). A package holds the workflow, leg, segment and handler
// documents with their `artefacts` pins, the activity contracts, rules and maps, a manifest, the
// engine binding, the test pack and the conformance pack (R-09-01). Temporal and DTFx run the
// documents on an interpreter; Flowable gets generated BPMN. The same input gives the same bytes.

export type Target = 'temporal' | 'dtfx' | 'flowable' | 'elsa';

export interface ExportRequest {
  workflows: string[];
  target: Target;
  tenant?: string;
  includeTests?: boolean;
  /** A test deployment: each worker carries the simulators of the ports its tasks reach. Never promotable (R-07-12). */
  withSimulators?: boolean;
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
  const stored = env.store.byKind('engine-binding').map((e) => e.doc).find((b) => b.target === req.target);
  if (!stored) throw new Error(`No engine binding for ${req.target}`);
  // A copy: a scope the binding does not list yet (a new scheme) gets the queue the binding's rule
  // derives (R-09-05), and the package's binding says so.
  const binding = clone(stored);

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
      return d && (flows.has(l) || (d.implements?.of ?? []).some((o: string) => flows.has(o)));
    });
    if (!legs.length) continue;
    for (const l of legs) addFlow(l);
    for (const h of scheme.handlers ?? []) addFlow(h);
    files[`schemes/${scheme.scope}.yaml`] = toYaml(scheme);
  }
  // A leg the dispatch table does not name yet starts from the intake of the leg filling the same
  // slot (Issue I-24). The package carries that scheme and records the start it takes.
  const fallbackStarts: { leg: string; scheme: string; message: string; issue: string }[] = [];
  const schemeDocs = env.store.byKind('scheme').map((e) => e.doc);
  for (const w of req.workflows) {
    const d = env.store.doc(w);
    if (d?.wfKind !== 'leg' || schemeDocs.some((s) => (s.messages ?? []).some((m: any) => m.disposition?.start?.leg === w))) continue;
    const found = startEntryFor(schemeDocs, w, (n) => env.store.doc(n));
    if (!found) continue;
    files[`schemes/${found.scheme.scope}.yaml`] = toYaml(found.scheme);
    fallbackStarts.push({ leg: w, scheme: found.scheme.scope, message: found.entry.id, issue: 'I-24' });
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

  // Intake maps the front door runs for the schemes in the package are tasks too.
  for (const f of Object.keys(files).filter((k) => k.startsWith('schemes/'))) {
    const scheme = env.store.scheme(f.slice('schemes/'.length, -'.yaml'.length));
    for (const m of scheme?.messages ?? []) {
      const map = m.disposition?.start?.intakeMap;
      if (map && !blocks.has(map)) {
        const d = env.store.doc(map);
        blocks.set(map, d);
        files[`activities/${map}@${d.release}.yaml`] = toYaml(d);
        tasks.push({ kind: 'activity', task: `${map}@${d.release}`, queue: queueOf(binding, map), binding: d.implementation?.binding, invokedBy: 'front-door' });
      }
    }
  }
  // The front door's workflow-select decision picks the platform workflow a start message runs.
  if (Object.keys(files).some((k) => k.startsWith('schemes/'))) {
    const sel = env.store.latest('p.table.workflow-select.v1');
    if (sel) files[`rules/${sel.name}@${sel.release}.yaml`] = toYaml(sel.doc);
  }
  // Separate workers, one per task queue (R-09-05): each serves only its queue's tasks.
  Object.assign(files, workerFiles(env, checker, binding, blocks, !!req.withSimulators));

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
    containsSimulators: !!req.withSimulators,
    promotable: !req.withSimulators && issues.filter((i) => i.severity === 'error').length === 0,
    workers: [...new Set(tasks.filter((t) => t.kind === 'activity').map((t) => t.queue))].sort().map((q) => ({ queue: q, path: `workers/${q}/`, tasks: tasks.filter((t) => t.kind === 'activity' && t.queue === q).map((t) => t.task).sort() })),
    flows: flowEntries,
    changeSets: changeSetsOf(env, files),
    ...(fallbackStarts.length ? { fallbackStarts } : {}),
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

/**
 * The change sets the package's artefacts were published in (R-11-20): those the studio recorded at
 * approval, and for content that came with the spec (the factory and the example bank), its pack
 * release. Every artefact file of the package is in exactly one.
 */
function changeSetsOf(env: StudioEnv, files: Record<string, string | Buffer>) {
  const recorded = (env.store.extra.changeSets ?? []) as any[];
  const sets = new Map<string, { id: string; kind: string; publishedAt?: string; checker?: string; makers?: string[]; members: string[] }>();
  const seen = new Set<string>();
  for (const f of Object.keys(files).sort()) {
    const m = /^(?:workflows|activities|rules|ports|extensions|hooks|simulators)\/(.+)@([0-9]+\.[0-9]+\.[0-9]+)\.yaml$/.exec(f) ?? /^workers\/[^/]+\/(?:artefacts\/[^/]+|simulators)\/(.+)@([0-9]+\.[0-9]+\.[0-9]+)\.yaml$/.exec(f);
    const sch = /^schemes\/(.+)\.yaml$/.exec(f);
    const name = m?.[1] ?? (sch ? sch[1] : undefined);
    if (!name) continue;
    const release = m?.[2] ?? env.store.scheme(name)?.release;
    const member = sch ? `scheme:${name}@${release}` : `${name}@${release}`;
    if (seen.has(member)) continue;
    seen.add(member);
    const cs = recorded.find((c) => c.members.some((x: any) => x.name === (sch ? `scheme:${name}` : name) && x.release === release));
    let id: string;
    if (cs) {
      id = cs.id;
      if (!sets.has(id)) sets.set(id, { id, kind: 'approval', publishedAt: cs.publishedAt, checker: cs.checker, makers: cs.makers, members: [] });
    } else {
      const scope = sch ? name : scopeOf(name);
      const pack = env.store.byKind('pack').find((p) => p.doc.scope === scope)?.doc;
      id = `pack:${scope}@${pack?.release ?? release}`;
      if (!sets.has(id)) sets.set(id, { id, kind: 'pack', members: [] });
    }
    sets.get(id)!.members.push(member);
  }
  return [...sets.values()].sort((a, b) => a.id.localeCompare(b.id));
}

function queueOf(binding: any, name: string): string {
  const scope = scopeOf(name);
  binding.queues ??= {};
  return (binding.queues[scope] ??= queueName(scope));
}

import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import * as esbuild from 'esbuild';
import ts from 'typescript';
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

// ------------------------------------------------------------------- separate workers

let bundled: string | undefined;

/** The worker program, bundled once into one file with no dependency (esbuild). */
export function workerBundle(): string {
  if (bundled) return bundled;
  const here = path.dirname(fileURLToPath(import.meta.url));
  const r = esbuild.buildSync({
    entryPoints: [path.join(here, '..', 'worker', 'main.ts')],
    bundle: true, platform: 'node', format: 'cjs', target: 'node22', write: false, minify: false,
    external: ['typescript', '@anthropic-ai/sdk'], legalComments: 'none', logLevel: 'error',
    banner: { js: '// Generated by the TransactHub Designer Studio: the activity worker runtime. Do not edit.' },
  });
  bundled = r.outputFiles[0].text;
  return bundled;
}

function workerFiles(env: StudioEnv, checker: Checker, binding: any, blocks: Map<string, any>, withSimulators: boolean): Record<string, string> {
  const files: Record<string, string> = {};
  const byQueue = new Map<string, any[]>();
  for (const [n, d] of blocks) {
    if (d?.kind !== 'activity' || d.implementation?.binding === 'subflow') continue; // a composite is expanded, never a task
    const q = queueOf(binding, n);
    if (!byQueue.has(q)) byQueue.set(q, []);
    byQueue.get(q)!.push(d);
  }
  const bundle = workerBundle();
  const pyHarness = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'interp', 'py_harness.py');
  for (const [q, acts] of [...byQueue].sort(([a], [b]) => a.localeCompare(b))) {
    const base = `workers/${q}/`;
    const docs = new Map<string, any>();
    const add = (d: any) => d?.name && !docs.has(`${d.name}@${d.release}`) && docs.set(`${d.name}@${d.release}`, d);
    for (const a of acts) {
      add(a);
      for (const p of a.ports ?? []) add(env.store.doc(p));
      // Rule sets, rules and tables are data the worker evaluates; they travel with it.
      const visit = (name: string, depth = 0) => {
        const x = env.store.doc(name);
        if (!x || depth > 4) return;
        add(x);
        for (const r of [...(x.ruleSets ?? []), ...(x.rules ?? []), ...(x.map?.edgeRules ?? [])]) visit(r, depth + 1);
      };
      for (const r of [...(a.ruleSets ?? []), ...(a.map?.edgeRules ?? [])]) visit(r);
    }
    for (const e of env.store.byKind('extension')) add(e.doc);
    const sims: any[] = [];
    if (withSimulators) {
      const ports = new Set(acts.flatMap((a) => a.ports ?? []));
      for (const s of env.store.byKind('simulator')) if (ports.has(s.doc.port)) sims.push(s.doc);
    }
    files[`${base}manifest.json`] = JSON.stringify({ queue: q, tasks: acts.map((a) => `${a.name}@${a.release}`).sort(), simulators: sims.map((s) => s.name).sort() }, null, 2) + '\n';
    files[`${base}worker.cjs`] = bundle;
    for (const [k, d] of [...docs].sort(([a], [b]) => a.localeCompare(b))) files[`${base}artefacts/${d.kind}/${k}.yaml`] = toYaml(d);
    for (const s of sims.sort((a, b) => a.name.localeCompare(b.name))) files[`${base}simulators/${s.name}@${s.release}.yaml`] = toYaml(s);
    let needsPython = false;
    for (const a of acts) {
      const sc = env.scripts.find(a.name);
      if (!sc) continue;
      if (sc.language === 'python') {
        needsPython = true;
        files[`${base}scripts/${a.name}.py`] = sc.source;
      } else {
        // Scripts are compiled at export, so a worker needs no TypeScript compiler.
        files[`${base}scripts/${a.name}.js`] = ts.transpileModule(sc.source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }, fileName: sc.file }).outputText;
      }
    }
    if (needsPython && fs.existsSync(pyHarness)) files[`${base}py_harness.py`] = fs.readFileSync(pyHarness, 'utf8');
    void checker;
  }
  return files;
}
