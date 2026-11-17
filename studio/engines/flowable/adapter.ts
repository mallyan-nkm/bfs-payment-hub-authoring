// The Flowable adapter for the conformance kit runner (C22-S2, C23-S2).
//
// For one scenario it generates BPMN with the studio's generator (src/export/bpmn.ts) from the
// expanded form of every workflow the scenario runs (R-09-17), writes a plan for the binding, runs
// the Java harness (an embedded Flowable engine on H2 in memory), and returns what the harness
// observed. The kit runner compares that observation with the expected trace, unchanged.

import { execFileSync, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { BuiltScenario, KitAdapter, KitObservation } from '../../src/conformance/kit.js';
import { DEFAULT_CALENDAR, durationMs } from '../../src/core/duration.js';
import { callsComposite, expandWorkflow } from '../../src/core/expand.js';
import { compareReleases } from '../../src/core/names.js';
import { generateBpmn, safe, SIGNAL_MESSAGES } from '../../src/export/bpmn.js';
import { Run, type RunConfig } from '../../src/interp/interpreter.js';
import type { StudioEnv } from '../../src/env.js';

const here = path.dirname(fileURLToPath(import.meta.url));
export const FLOWABLE_DIR = here;
const HARNESS = path.join(here, 'harness');
const MVN = path.join(here, '.bin', 'apache-maven-3.9.9', 'bin', 'mvn');

/** Build the Java harness once, offline when the local repository already holds Flowable. */
export function buildHarness(force = false) {
  const classes = path.join(HARNESS, 'target', 'classes', 'th', 'flowable', 'Harness.class');
  const lib = path.join(HARNESS, 'target', 'lib');
  const sources = path.join(HARNESS, 'src', 'main', 'java', 'th', 'flowable');
  const newest = Math.max(...fs.readdirSync(sources).map((f) => fs.statSync(path.join(sources, f)).mtimeMs));
  if (!force && fs.existsSync(classes) && fs.existsSync(lib) && fs.statSync(classes).mtimeMs >= newest) return;
  if (!fs.existsSync(MVN)) throw new Error(`Maven is not installed: run ${path.join(here, 'setup.sh')} first`);
  const args = ['-q', '-B', `-Dmaven.repo.local=${path.join(here, '.bin', 'm2')}`, 'package'];
  const r = spawnSync(MVN, args, { cwd: HARNESS, encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`The harness did not build:\n${r.stdout}\n${r.stderr}`);
}

const keyOf = (name: string, release: string) => safe(`${name}@${release}`);

export interface FlowableOptions { outDir?: string; keep?: boolean }

export function flowableAdapter(env: StudioEnv, opts: FlowableOptions = {}): KitAdapter {
  const outRoot = opts.outDir ?? path.join(here, 'out');
  return {
    target: 'flowable',
    async run(b: BuiltScenario): Promise<KitObservation> {
      buildHarness();
      const dir = path.join(outRoot, b.id);
      fs.rmSync(dir, { recursive: true, force: true });
      fs.mkdirSync(dir, { recursive: true });
      const plan = makePlan(env, b, dir);
      const planFile = path.join(dir, 'plan.json');
      fs.writeFileSync(planFile, JSON.stringify(plan, null, 2));
      const cp = [path.join(HARNESS, 'target', 'classes'), path.join(HARNESS, 'target', 'lib', '*')].join(path.delimiter);
      let stdout: string;
      try {
        stdout = execFileSync('java', ['-Xmx2g', '-cp', cp, 'th.flowable.Harness', planFile], { encoding: 'utf8', maxBuffer: 1 << 30, stdio: ['ignore', 'pipe', 'pipe'], timeout: 600_000 });
      } catch (e: any) {
        fs.writeFileSync(path.join(dir, 'harness.err'), String(e.stderr ?? e.message));
        throw new Error(`The Flowable harness failed: ${String(e.stderr ?? e.message).split('\n').filter((l: string) => !l.startsWith('Picked up')).slice(0, 8).join(' ')}`);
      }
      const line = stdout.split('\n').find((l) => l.startsWith('OBSERVATION '));
      if (!line) throw new Error('The Flowable harness gave no observation');
      const obs = JSON.parse(line.slice('OBSERVATION '.length));
      fs.writeFileSync(path.join(dir, 'observation.json'), JSON.stringify(obs, null, 2));
      return obs as KitObservation;
    },
  };
}

/** Everything the binding needs for one scenario, with the BPMN written beside it. */
export function makePlan(env: StudioEnv, b: BuiltScenario, dir: string) {
  const cfg: RunConfig = { ...b.config, store: env.store, scripts: env.scripts, documents: b.documents } as RunConfig;
  const run = new Run(cfg); // used for resolution only: kit documents first, then the store
  const sc = b.spec;
  const docs = b.documents;
  const releasesOf = (name: string): string[] => Object.keys(docs).filter((k) => k.startsWith(name + '@')).map((k) => k.slice(name.length + 1)).sort(compareReleases);
  const availableFromMs = (name: string, release: string) => (cfg.availableFrom?.[`${name}@${release}`] ? durationMs(cfg.availableFrom[`${name}@${release}`]) : 0);
  const resolverFor = (doc: any) => {
    const pins = new Map<string, string>((doc.artefacts ?? []).map((a: any) => [a.name, a.release]));
    return (name: string) => (pins.has(name) ? run.resolveDoc(name, pins.get(name)) : run.resolveDoc(name));
  };
  const processes: { key: string; file: string; name: string; release: string }[] = [];
  const generated = new Set<string>();
  const generate = (doc: any, asHook = false) => {
    const key = keyOf(doc.name, doc.release);
    if (generated.has(key)) return key;
    generated.add(key);
    const resolve = resolverFor(doc);
    let wf = asHook ? { ...doc, kind: 'workflow', exit: { outcomes: doc.outcomes ?? [] } } : doc;
    // The engine receives the expanded form only (R-09-17).
    if (callsComposite(wf, resolve)) wf = expandWorkflow(wf, resolve);
    if (asHook) wf = { ...wf, kind: 'hook' };
    const xml = generateBpmn(wf, { queueOf: (n) => `th-${n.split('.').slice(0, 2).join('-')}`, resolve });
    const file = path.join(dir, `${key}.bpmn20.xml`);
    fs.writeFileSync(file, xml);
    processes.push({ key, file, name: doc.name, release: doc.release });
    return key;
  };

  // The workflows the scenario starts, every release it names.
  const instances: { label: string; key: string; atMs: number }[] = [];
  let handler: any = null;
  if (sc.handler) {
    handler = { doc: run.resolveDoc(b.workflow) };
  } else if (sc.instances) {
    for (const i of sc.instances) {
      const d = run.resolveDoc(b.workflow, i.workflowRelease);
      instances.push({ label: i.label, key: generate(d), atMs: durationMs(i.at) });
    }
  } else {
    const d = run.resolveDoc(b.workflow);
    instances.push({ label: 'root', key: generate(d), atMs: 0 });
  }

  // Legs that can fill a slot of the platform workflow: by the rail the route decision names.
  const schemes = [...env.store.byKind('scheme').map((e) => e.doc), ...Object.values<any>(docs).filter((d) => d?.kind === 'scheme')];
  const legs: Record<string, Record<string, { byRail: Record<string, string>; fallback?: string }>> = {};
  const releases: Record<string, { release: string; key: string; availableFromMs: number }[]> = {};
  const addLeg = (name: string) => {
    if (releases[name]) return;
    const rels = releasesOf(name);
    const list = rels.length ? rels : [run.resolveDoc(name)?.release].filter(Boolean);
    releases[name] = list.map((r: string) => ({ release: r, key: generate(run.resolveDoc(name, r)), availableFromMs: availableFromMs(name, r) }));
  };
  for (const inst of instances) {
    const wf = processes.find((p) => p.key === inst.key)!;
    const doc = run.resolveDoc(wf.name, wf.release);
    for (const slot of doc.slots ?? []) {
      const entry: { byRail: Record<string, string>; fallback?: string } = { byRail: {} };
      for (const s of schemes) {
        for (const l of s.legs ?? []) {
          const d = run.resolveDoc(l);
          if (d?.implements?.slot === slot.name && (d.implements.of ?? []).includes(doc.name)) { entry.byRail[s.scope] ??= l; addLeg(l); }
        }
      }
      for (const d of Object.values<any>(docs)) {
        if (d?.kind === 'workflow' && d.wfKind === 'leg' && d.implements?.slot === slot.name && (d.implements.of ?? []).includes(doc.name)) { entry.fallback ??= d.name; addLeg(d.name); }
      }
      (legs[doc.name] ??= {})[slot.name] = entry;
    }
  }

  // Hook bindings (the tenant's), each bound hook generated at its bound release.
  const bindings = (cfg.bindings ?? []).map((x: any) => {
    const hook = run.resolveDoc(x.hook, x.release);
    return { hookPoint: x.hookPoint, hook: x.hook, release: x.release, validFrom: x.validFrom, validTo: x.validTo, key: hook ? generate(hook, true) : keyOf(x.hook, x.release) };
  });

  // The message catalogue the front door reads.
  const catalogue: Record<string, any> = {};
  for (const s of schemes) for (const m of s.messages ?? []) catalogue[m.id] ??= { start: !!m.disposition?.start, handler: m.disposition?.handler, direction: m.direction };

  const latency: Record<string, number> = {};
  for (const [k, v] of Object.entries<any>(cfg.stubs ?? {})) if (v?.latency) latency[k] = durationMs(v.latency);

  return {
    scenario: b.id,
    studioRoot: env.studioRoot,
    startMs: Date.parse(`${cfg.evaluationDate}T${cfg.startTime ?? '06:00:00'}Z`),
    conversation: b.context?.header?.conversation ?? 'conv-1',
    context: b.context,
    processes,
    instances,
    handler,
    legs,
    releases,
    bindings,
    catalogue,
    latency,
    signals: (cfg.signals ?? []).map((s) => ({ atMs: durationMs(s.after), signal: s.signal })),
    signalMessages: SIGNAL_MESSAGES,
    supersessions: (cfg.supersessions ?? []).map((s) => ({ artefact: s.artefact, replaces: s.replaces, with: s.with, id: s.id, ...(s.effectiveAt ? { effectiveAtMs: durationMs(s.effectiveAt) } : {}) })),
    calendar: DEFAULT_CALENDAR,
  };
}
