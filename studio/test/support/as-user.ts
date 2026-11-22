import { dispatch, type Req } from '../../src/server/api.js';
import type { StudioEnv } from '../../src/env.js';

// In-process calls of the API, as the server makes them once a user has signed in (R-11-32): the
// server sets `user` from the session. A white-box test names the user the way the black-box client
// does: the actor, checker or maker the body names, else the maker of the tenant the call names.
// `user: null` sends no user, for the 401 tests.

const DEFAULT_USER: Record<string, string> = { owner: 'shared.designer', 'example-bank': 'example.maker', 'second-bank': 'second.maker' };

export function userFor(req: Omit<Req, 'user'>): string {
  const named = req.body?.actor ?? req.body?.checker ?? req.body?.maker ?? req.query?.actor;
  if (named) return named;
  const tenant = req.body?.tenant ?? req.query?.tenant ?? /\/api\/tenants\/([^/]+)/.exec(req.path)?.[1];
  return DEFAULT_USER[tenant ?? 'owner'] ?? 'shared.designer';
}

export function dispatchAs(env: StudioEnv, req: Omit<Req, 'user'> & { user?: string | null }) {
  const user = req.user === null ? undefined : req.user ?? userFor(req);
  return dispatch(env, { ...req, query: req.query ?? {}, user });
}
