import * as fs from 'node:fs';
import * as path from 'node:path';
import { CONTENT_DIR, envFor } from '../env.js';
import { ContentStore } from './content-store.js';
import { LOCAL_REPO, startLocalContent } from './local.js';
import { seedContent, seedPlan } from './seed.js';

// Regenerate this repository's content-configurations/ (R-11-25): the seed command runs as a change
// set against an in-memory fake GitHub seeded from the folder, and the files of that one commit are
// copied back. It adds files and never rewrites one (R-11-35): a seeded file whose source in spec/ changed at the same
// release stops it, as on a connected branch, and names the file. Release the change, then seed again.

export async function writeLocalSeed(actor?: string) {
  const local = await startLocalContent();
  try {
    const full = `${LOCAL_REPO.owner}/${LOCAL_REPO.repo}`;
    const content = await ContentStore.open(local.connection);
    const env = envFor(content, { banks: [] });
    const plan = seedPlan(env);
    if (!plan.add.length && !plan.clash.length && !plan.replace.length && !plan.users) return { status: 'unchanged' as const, unchanged: plan.same, written: [] as string[], replaced: [] as string[], changeSet: undefined };
    const r = await seedContent(env, actor);
    const replaced = r.replaced ?? [];
    const files = local.fake.files(full, LOCAL_REPO.branch);
    const commit = content.writes.find((w) => w.commit === r.commit);
    const written: string[] = [];
    for (const p of commit?.files ?? []) {
      const rel = p.replace(/^content-configurations\//, '');
      const disk = path.join(CONTENT_DIR, rel);
      fs.mkdirSync(path.dirname(disk), { recursive: true });
      fs.writeFileSync(disk, files[p]);
      written.push(rel);
    }
    return { status: 'committed' as const, changeSet: r.changeSet, written, replaced, unchanged: r.unchanged };
  } finally {
    await local.stop();
  }
}
