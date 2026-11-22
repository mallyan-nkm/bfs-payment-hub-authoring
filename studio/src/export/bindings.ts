import { canonicalJson } from '../core/canonical.js';
import { compareReleases } from '../core/names.js';
import type { StudioEnv } from '../env.js';

// Engine bindings (Part 9; C20-S1, C21-S4). A binding is generated from the packs: one queue or
// topic per scope (R-09-05), task naming by exact release (R-09-04), and the target's own settings.
// The settings are environment placeholders the designer never edits (R-09-03).

const TARGETS: Record<string, { strategy: string; defaults?: any; extensions?: any }> = {
  temporal: { strategy: 'interpreter', defaults: { queueTimeout: 'PT30S', heartbeat: 'PT10S', forEachChunk: 500, forEachConcurrency: 64, historyLimit: 10000 } },
  dtfx: { strategy: 'interpreter', defaults: { forEachChunk: 500, forEachConcurrency: 64 }, extensions: { attemptTimeout: 'timer-race' } },
  flowable: { strategy: 'generation', extensions: { activityTask: 'external-worker', retry: 'modelled-loop' } },
  elsa: { strategy: 'interpreter', defaults: { forEachChunk: 500, forEachConcurrency: 64 }, extensions: { suspension: 'bookmark', replay: 'journal-in-workflow-state', attemptTimeout: 'timer-race' } },
};

/**
 * Each binding's current release (Issue I-83, owner's decision of 2026-11-22): a binding is released like every other artefact,
 * as `p.binding.<target>.v1`, and a change is a new release. 1.1.0 added forEachConcurrency (R-09-45).
 */
export const BINDING_RELEASES: Record<string, string> = { temporal: '1.1.0', dtfx: '1.1.0', flowable: '1.0.0', elsa: '1.1.0' };
export const bindingName = (target: string) => `p.binding.${target}.v1`;

/** The binding of an engine: its latest published release (a binding with no release only when none has one). */
export function bindingOf(env: StudioEnv, target: string): { doc: any; release: string; hash: string; name: string } | undefined {
  const all = env.store.byKind('engine-binding').filter((e) => e.doc.target === target && e.status !== 'draft' && e.status !== 'in-review');
  const released = all.filter((e) => e.doc.name && e.doc.release).sort((a, b) => compareReleases(b.doc.release, a.doc.release));
  const e = released[0] ?? all[0];
  return e ? { doc: e.doc, release: e.doc.release ?? '0.0.0', hash: e.hash, name: e.doc.name ?? `binding:${target}` } : undefined;
}

export function queueName(scope: string): string {
  return scope === 'b' ? 'th-bank' : `th-${scope.replace(/\./g, '-')}`;
}

export function generateBinding(env: StudioEnv, target: string): any {
  const t = TARGETS[target];
  if (!t) throw new Error(`Unknown target ${target}`);
  const scopes = env.store.byKind('pack').map((e) => e.doc.scope as string);
  const order = ['p', 'uae.p', 'uae.wps', 'b'];
  scopes.sort((a, b) => (order.indexOf(a) + 99) % 99 - (order.indexOf(b) + 99) % 99 || a.localeCompare(b));
  const b: any = { kind: 'engine-binding', name: bindingName(target), release: BINDING_RELEASES[target], target, strategy: t.strategy, queues: Object.fromEntries(scopes.map((s) => [s, queueName(s)])), taskNaming: '<name>@<release>' };
  if (t.defaults) b.defaults = t.defaults;
  if (t.extensions) b.extensions = t.extensions;
  return b;
}

/** Regenerate each binding and compare it with the example (C20-S1, C21-S4 success). */
export function compareBindings(env: StudioEnv) {
  return ['temporal', 'dtfx', 'flowable', 'elsa'].map((target) => {
    const want = bindingOf(env, target)?.doc;
    const got = generateBinding(env, target);
    return { target, equal: !!want && canonicalJson(want) === canonicalJson(got), generated: got };
  });
}
