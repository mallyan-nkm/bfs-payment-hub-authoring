import { canonicalJson } from '../core/canonical.js';
import type { StudioEnv } from '../env.js';

// Engine bindings (Part 9; C20-S1, C21-S4). A binding is generated from the packs: one queue or
// topic per scope (R-09-05), task naming by exact release (R-09-04), and the target's own settings.
// The settings are environment placeholders the designer never edits (R-09-03).

const TARGETS: Record<string, { strategy: string; defaults?: any; extensions?: any }> = {
  temporal: { strategy: 'interpreter', defaults: { queueTimeout: 'PT30S', heartbeat: 'PT10S', forEachChunk: 500, historyLimit: 10000 } },
  dtfx: { strategy: 'interpreter', defaults: { forEachChunk: 500 }, extensions: { attemptTimeout: 'timer-race' } },
  flowable: { strategy: 'generation', extensions: { activityTask: 'external-worker', retry: 'modelled-loop' } },
};

export function queueName(scope: string): string {
  return scope === 'b' ? 'th-bank' : `th-${scope.replace(/\./g, '-')}`;
}

export function generateBinding(env: StudioEnv, target: string): any {
  const t = TARGETS[target];
  if (!t) throw new Error(`Unknown target ${target}`);
  const scopes = env.store.byKind('pack').map((e) => e.doc.scope as string);
  const order = ['p', 'uae.p', 'uae.wps', 'uae.aani', 'b'];
  scopes.sort((a, b) => (order.indexOf(a) + 99) % 99 - (order.indexOf(b) + 99) % 99 || a.localeCompare(b));
  const b: any = { kind: 'engine-binding', target, strategy: t.strategy, queues: Object.fromEntries(scopes.map((s) => [s, queueName(s)])), taskNaming: '<name>@<release>' };
  if (t.defaults) b.defaults = t.defaults;
  if (t.extensions) b.extensions = t.extensions;
  return b;
}

/** Regenerate each binding and compare it with the example (C20-S1, C21-S4 success). */
export function compareBindings(env: StudioEnv) {
  return ['temporal', 'dtfx', 'flowable'].map((target) => {
    const want = env.store.byKind('engine-binding').find((e) => e.doc.target === target)?.doc;
    const got = generateBinding(env, target);
    return { target, equal: !!want && canonicalJson(want) === canonicalJson(got), generated: got };
  });
}
