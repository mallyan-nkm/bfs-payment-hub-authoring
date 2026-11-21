import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Store, StoreError } from './core/store.js';
import { SchemaValidator } from './core/schema.js';
import { ScriptRegistry } from './interp/sandbox.js';
import { ContentStore, NOT_CONNECTED, connectionFromEnv, type ContentConnection } from './content/content-store.js';
import type { RunnerEnv } from './testing/runner.js';

// Where things are. The spec repository sits beside the studio, at ../spec: its schemas, its
// conformance kit (the engines' test fixture) and its factory sources. The studio's content is not
// here: it is read from a GitHub branch into memory (R-11-21), and nothing is written to disk.
const here = path.dirname(fileURLToPath(import.meta.url));
export const STUDIO_ROOT = path.resolve(here, '..');
export const SPEC_ROOT = process.env.TH_SPEC_ROOT ?? path.resolve(STUDIO_ROOT, '..', 'spec');
/** This repository's copy of the content branch's folder: what `npm run studio:dev` and the tests seed the fake GitHub from. */
export const CONTENT_DIR = path.resolve(STUDIO_ROOT, '..', 'content-configurations');

export interface StudioEnv extends RunnerEnv {
  schemas: SchemaValidator;
  specRoot: string;
  studioRoot: string;
}

/** The bank packs named in `TH_BANKS` (comma separated). */
export function banksFromEnv(): string[] {
  return (process.env.TH_BANKS ?? '').split(',').map((s) => s.trim()).filter(Boolean);
}

/** A studio environment over a content store that is already read (R-11-21). `banks` adds bank packs by folder name. */
export function envFor(content: ContentStore, opts: { banks?: string[] } = {}): StudioEnv {
  const store = Store.fromContent(content, SPEC_ROOT, { banks: opts.banks ?? banksFromEnv() });
  return {
    store,
    // The factory's scripts ship with the studio; a designer's are files on the branch, read into memory.
    scripts: new ScriptRegistry([path.join(STUDIO_ROOT, 'scripts')], () => store.scriptSources.values()),
    samplesDirs: [path.join(STUDIO_ROOT, 'fixtures', 'samples')],
    schemas: new SchemaValidator(path.join(SPEC_ROOT, 'schemas')),
    specRoot: SPEC_ROOT,
    studioRoot: STUDIO_ROOT,
  };
}

/**
 * The studio environment over the connected content branch: `TH_CONTENT_REPO`, `TH_CONTENT_BRANCH`,
 * `TH_GITHUB_TOKEN` and optionally `TH_GITHUB_API` (R-11-21, R-11-24). Without a connection it fails:
 * the studio keeps no local copy.
 */
export async function openEnv(opts: { banks?: string[]; connection?: ContentConnection } = {}): Promise<StudioEnv> {
  const conn = opts.connection ?? connectionFromEnv();
  if (!conn) throw new StoreError('content', NOT_CONNECTED);
  return envFor(await ContentStore.open(conn), opts);
}

/**
 * For the commands that only read and run (check, test, kit, the engine adapters): the connected
 * branch when `TH_CONTENT_REPO` is set, else an in-memory fake GitHub seeded from this repository's
 * `content-configurations/`. Neither writes to disk. `where` says which, for the command's output.
 */
export async function openReadEnv(opts: { banks?: string[] } = {}): Promise<{ env: StudioEnv; where: string; stop: () => Promise<void> }> {
  const conn = connectionFromEnv();
  if (conn) return { env: await openEnv({ ...opts, connection: conn }), where: `${conn.owner}/${conn.repo} ${conn.branch}`, stop: async () => {} };
  const { startLocalContent } = await import('./content/local.js');
  const local = await startLocalContent({ unref: true });
  return { env: envFor(await ContentStore.open(local.connection), opts), where: `an in-memory branch seeded from ${path.relative(process.cwd(), CONTENT_DIR) || CONTENT_DIR}`, stop: local.stop };
}
