import { OWNER_TENANT, tenantView, viewTenant, type Store } from './store.js';
import type { ScriptRegistry } from '../interp/sandbox.js';

// One tenant's studio (R-11-48, Issue I-120). The studio is multi-tenant: a bank's users read, test, export and deploy only
// shared content and their bank's own. Every API call of a bank's user, every test run and every export for a tenant works
// on this view, so no route, tool or report it reaches can read another bank's artefact, script, sample, test or draft.

/** An environment whose store and scripts are one tenant's view. Idempotent: a view of the same tenant is returned as is. */
export function scopedEnv<E extends { store: Store; scripts: ScriptRegistry }>(env: E, tenant: string): E {
  const store = tenantView(env.store, tenant);
  if (store === env.store) return env;
  return { ...env, store, scripts: env.scripts.over(() => store.scriptSources.values()) };
}

/** The tenant an environment is scoped to, or undefined when it is the whole studio (the owner's). */
export const scopeOf = (env: { store: Store }): string | undefined => viewTenant(env.store);

/** The tenant a run is for: its test's, else the owner's shared content only. */
export const runTenant = (test: any): string => (test?.tenant && typeof test.tenant === 'string' ? test.tenant : OWNER_TENANT);
