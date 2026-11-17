import * as fs from 'node:fs';
import * as path from 'node:path';
import { StoreError } from '../core/store.js';
import { parseName } from '../core/names.js';
import { lintScript } from '../interp/sandbox.js';
import type { StudioEnv } from '../env.js';
import { enforce } from './governance.js';

// Scripts-behind and sample files a designer writes while authoring (C07, C12-S4). Both live in the
// workspace beside the drafts. A script belongs to an activity draft: once the activity is
// published its behaviour is fixed, so its script is too (R-01-10).

const PY_FORBIDDEN = [/\bimport\s+(os|sys|time|random|socket|subprocess|datetime)\b/, /\bfrom\s+(os|sys|time|random|socket|subprocess|datetime)\b/, /\bopen\(/];

function workspace(env: StudioEnv, sub: string): string {
  const root = env.store.dataRoot;
  if (!root || root === path.resolve('')) throw new StoreError('workspace', 'The studio has no workspace to write to (TH_DATA_ROOT)');
  const d = path.join(root, sub);
  fs.mkdirSync(d, { recursive: true });
  return d;
}

export function saveScript(env: StudioEnv, inp: { tenant: string; actor: string; activity: string; language?: 'typescript' | 'python'; source: string }) {
  const parsed = parseName(inp.activity);
  if (!parsed) throw new StoreError('L01', `${inp.activity} is not an artefact name`);
  enforce(env, inp.actor, inp.tenant, 'author', `script:${inp.activity}`, parsed.scope);
  const draft = env.store.drafts(inp.tenant).find((e) => e.name === inp.activity && e.kind === 'activity');
  if (!draft) throw new StoreError('not-found', `No activity draft ${inp.activity} in tenant ${inp.tenant}: a script belongs to an activity being authored`);
  if (env.store.releasesOf(inp.activity, { tenant: inp.tenant }).some((e) => e.status === 'published')) throw new StoreError('R-01-10', `${inp.activity} is published; its script is fixed. Release a new major or a new activity`);
  const language = inp.language ?? draft.doc.implementation?.language ?? 'typescript';
  if (draft.doc.implementation?.binding !== 'script') throw new StoreError('C07', `${inp.activity} is not bound to a script (implementation.binding is ${draft.doc.implementation?.binding ?? 'unset'})`);
  if (draft.doc.implementation?.language && draft.doc.implementation.language !== language) throw new StoreError('C07', `${inp.activity} declares ${draft.doc.implementation.language}, not ${language}`);
  const problems = language === 'typescript' ? lintScript(inp.source) : PY_FORBIDDEN.filter((re) => re.test(inp.source)).map((re) => `The script uses ${re.source}, outside the injected interface`);
  const entry = language === 'typescript' ? /export\s+(async\s+)?function\s+run\s*\(/ : /^(async\s+)?def\s+run\s*\(/m;
  if (!entry.test(inp.source)) problems.push(language === 'typescript' ? 'A script exports `run({ ctx, ports, rules, params, clock, ids, log })` and returns the outcome' : 'A script defines `run(io)` and returns the outcome');
  if (problems.length) {
    env.store.record({ tenant: inp.tenant, actor: inp.actor, action: 'save-script', key: inp.activity, outcome: 'refused', detail: problems.join('; ') });
    throw new StoreError('C07', problems.join('; '));
  }
  const file = path.join(workspace(env, 'scripts'), `${inp.activity}.${language === 'python' ? 'py' : 'ts'}`);
  fs.writeFileSync(file, inp.source);
  env.store.record({ tenant: inp.tenant, actor: inp.actor, action: 'save-script', key: inp.activity, detail: path.basename(file) });
  return { activity: inp.activity, language, file: path.basename(file) };
}

const SAMPLE_NAME = /^[a-z0-9][a-z0-9-]*\.[a-z0-9]+$/;

/** A synthetic sample file for tests. The designer vouches it holds no personal data. */
export function saveSample(env: StudioEnv, inp: { tenant: string; actor: string; name: string; content: string }) {
  enforce(env, inp.actor, inp.tenant, 'author', `sample:${inp.name}`);
  if (!SAMPLE_NAME.test(inp.name ?? '')) throw new StoreError('C12', 'A sample name is lower case, digits and hyphens, with an extension, for example demo-file-valid.csv');
  if (typeof inp.content !== 'string' || !inp.content.length || inp.content.length > 1_000_000) throw new StoreError('C12', 'A sample is text of at most 1 MB');
  const dir = workspace(env, 'samples');
  for (const d of env.samplesDirs) if (path.resolve(d) !== path.resolve(dir) && fs.existsSync(path.join(d, inp.name))) throw new StoreError('R-01-10', `samples/${inp.name} is factory content; choose another name`);
  fs.writeFileSync(path.join(dir, inp.name), inp.content);
  env.store.record({ tenant: inp.tenant, actor: inp.actor, action: 'save-sample', key: `samples/${inp.name}` });
  return { sample: `samples/${inp.name}` };
}
