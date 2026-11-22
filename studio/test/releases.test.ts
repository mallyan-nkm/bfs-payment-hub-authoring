import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { checkAll, releaseLedgerIssues } from '../src/checks/checks.js';
import { ContentStore } from '../src/content/content-store.js';
import { ledgerPath, seedContent, seedFiles, seedPlan, textSha } from '../src/content/seed.js';
import { CONTENT_DIR, SPEC_ROOT, envFor } from '../src/env.js';
import { parseYamlAll } from '../src/core/yaml.js';
import { freshEnv } from './helpers.js';
import { BRANCH, connection, fake, forkRepo } from './support/content.js';

// Published releases never change (R-11-35, R-11-36): a factory artefact changed after its first publish is a new release beside
// the earlier one, which stays as it was published; the seed adds files and never rewrites one; and every seeded file is in its
// change set's ledger with the SHA-256 of its text.

const ROOT = 'content-configurations';
const disk = (p: string) => fs.readFileSync(path.join(CONTENT_DIR, p), 'utf8');
const body = (t: string) => parseYamlAll(t.slice(t.indexOf('\n') + 1)).find((d: any) => d && typeof d === 'object');

describe('every changed factory artefact is a new release; the earlier stays as it was published (R-11-35)', () => {
  it('keeps 1.0.0 of each changed artefact beside its new release, and resolves the latest', () => {
    const env = freshEnv();
    for (const [name, now] of [['uae.wps.exchange.return-credits.v1', '1.1.0'], ['p.post.accounting.v1', '1.1.0'], ['uae.wps.table.returns.v1', '1.1.0'], ['uae.wps.map.pif-in.v1', '1.1.0']]) {
      expect(env.store.releasesOf(name).map((e) => e.release), name).toEqual(['1.0.0', now]);
      expect(env.store.get(name)!.release).toBe(now);
    }
    // 1.0.0 is the text of the first publish: no replyWait, which came later (Issue I-41).
    expect(env.store.get('uae.wps.exchange.return-credits.v1', '1.0.0')!.doc.parameters).toBeUndefined();
    expect(env.store.get('uae.wps.exchange.return-credits.v1')!.doc.parameters.replyWaitCalendar.default).toBe('elapsed');
  });

  it('gives an interface change a new major line, and leaves the earlier line where it was', () => {
    const env = freshEnv();
    for (const n of ['uae.wps.decide.returns', 'uae.wps.map.prc-out', 'uae.wps.map.dif-out', 'uae.wps.map.rtc-in', 'uae.wps.wf.agent-payment-in']) {
      expect(env.store.get(`${n}.v1`)!.release, n).toBe('1.0.0');
      expect(env.store.get(`${n}.v2`)!.release, n).toBe('2.0.0');
    }
    expect(env.store.scheme('uae.wps').release).toBe('1.1.0');
    expect(env.store.scheme('uae.wps').messages.find((m: any) => m.id === 'uae.wps.pif').disposition.start.leg).toBe('uae.wps.wf.agent-payment-in.v2');
  });

  it('moves the banks\' configuration sets and pins to the new pack releases', () => {
    const env = freshEnv({ banks: ['dib'] } as any);
    for (const t of ['example-bank', 'dib']) {
      expect(env.store.configurationSet(t).accepts, t).toEqual([{ scope: 'p', release: '1.2.0' }, { scope: 'uae.p', release: '1.2.0' }, { scope: 'uae.wps', release: '1.2.0' }]);
    }
    // The example bank's first configuration set and lock file stay as they were published.
    expect(body(disk('banks/example-bank/configuration-set.yaml')).release).toBe('1.0.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.1.0.yaml')).release).toBe('1.1.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.2.0.yaml')).release).toBe('1.2.0');
    expect(body(disk('banks/example-bank/packs/1.0.0.yaml')).release).toBe('1.0.0');
    expect(body(disk('banks/example-bank/packs/1.1.0.yaml')).release).toBe('1.1.0');
    expect(body(disk('banks/example-bank/pack.yaml')).release).toBe('1.2.0');
  });

  it('publishes a changed test again under its pack release; the studio reads the latest', () => {
    const env = freshEnv();
    expect(disk('artefacts/uae.wps/tests/WPS-IN-002.yaml')).not.toContain('report-disbursement.send');
    expect(disk('artefacts/uae.wps/tests/WPS-IN-002@1.1.0.yaml')).toContain('report-disbursement.send');
    expect(env.store.test('WPS-IN-002')!.doc.target).toBe('uae.wps.wf.agent-payment-in.v2');
  });
});

describe('the seed adds and never rewrites; the ledger holds what it published (R-11-35, R-11-36)', () => {
  it('this repository\'s copy is the seed: nothing to add, nothing that clashes, and every seeded file in a ledger with its hash', () => {
    const env = freshEnv();
    const plan = seedPlan(env);
    expect(plan.add.map((f) => f.path)).toEqual([]);
    expect(plan.clash.map((f) => f.path)).toEqual([]);
    expect(plan.replace).toEqual([]);
    expect(releaseLedgerIssues(env.store.content!.files)).toEqual([]);
    expect(checkAll(env).filter((i) => i.code === 'L41')).toEqual([]);
  });

  it('a factory change at a release already published stops the seed and names the file; nothing is written', async () => {
    const full = forkRepo();
    const at = `${ROOT}/artefacts/uae.wps/uae.wps.map.ack-in.v1/1.2.0.yaml`;
    fake().commitFiles(full, BRANCH, { [at]: fake().files(full, BRANCH)[at].replace('title: ACK or NAK to status', 'title: changed elsewhere') });
    const env = envFor(await ContentStore.open(connection(fake(), full)), { banks: [] });
    const before = fake().head(full, BRANCH);
    await expect(seedContent(env)).rejects.toThrow(/uae\.wps\.map\.ack-in\.v1\/1\.2\.0\.yaml.*never rewritten/);
    expect(fake().head(full, BRANCH)).toBe(before);
    // And the check names it: the published release changed since its ledger recorded it.
    expect(checkAll(env).filter((i) => i.code === 'L41').map((i) => i.file)).toEqual(['artefacts/uae.wps/uae.wps.map.ack-in.v1/1.2.0.yaml']);
  });

  it('a later seed adds its new files, keeps a replaced bank lock file first, and records both in its ledger', async () => {
    const full = forkRepo();
    // The branch as it was before this release: drop what the latest seed added, and put the earlier example bank lock back.
    const files = fake().files(full, BRANCH);
    const latest = Object.keys(files).filter((p) => p.startsWith(`${ROOT}/change-sets/ledger/`)).sort().pop()!;
    const led = parseYamlAll(files[latest])[0] as any;
    const undo: Record<string, string | null> = { [latest]: null };
    for (const f of led.files) undo[`${ROOT}/${f.path}`] = null;
    for (const r of led.replaced ?? []) undo[`${ROOT}/${r.path}`] = files[`${ROOT}/${r.keptAt}`];
    fake().commitFiles(full, BRANCH, undo, 'Before the release');
    const env = envFor(await ContentStore.open(connection(fake(), full)), { banks: [] });
    const r = await seedContent(env);
    expect(r.status).toBe('committed');
    const after = fake().files(full, BRANCH);
    const mine = parseYamlAll(after[`${ROOT}/${ledgerPath(r.changeSet!)}`])[0] as any;
    expect(mine.files.map((f: any) => f.path).sort()).toEqual(led.files.map((f: any) => f.path).sort());
    for (const f of mine.files) expect(textSha(after[`${ROOT}/${f.path}`]), f.path).toBe(f.sha256);
    expect(mine.replaced ?? []).toEqual(led.replaced ?? []);
    // Every file the seed would publish is on the branch as this repository holds it.
    for (const s of seedFiles(SPEC_ROOT)) expect(after[`${ROOT}/${s.path}`], s.path).toBe(s.text);
    expect(releaseLedgerIssues(env.store.content!.files)).toEqual([]);
  }, 60000);

  it('the engine bindings carry no release: a changed binding replaces their file, and the ledger names the hash it replaces', async () => {
    const full = forkRepo();
    const P = `${ROOT}/artefacts/bindings/bindings.yaml`;
    const now = fake().files(full, BRANCH)[P];
    // The branch as it was before the latest binding change (the bindings' own seed, CS-2026-11-22-005).
    const before = now.replace(/^ {2}forEachConcurrency: 64\n/gm, '');
    expect(before).not.toBe(now);
    fake().commitFiles(full, BRANCH, { [P]: before, [`${ROOT}/change-sets/ledger/CS-2026-11-22-005.yaml`]: null }, 'Before the bindings change');
    const env = envFor(await ContentStore.open(connection(fake(), full)), { banks: [] });
    expect(seedPlan(env).replace.map((r) => r.file.path)).toEqual([P.slice(ROOT.length + 1)]);
    const r = await seedContent(env);
    const after = fake().files(full, BRANCH);
    expect(after[P]).toBe(now);
    const led = parseYamlAll(after[`${ROOT}/${ledgerPath(r.changeSet!)}`])[0] as any;
    expect(led.replaced).toEqual([{ path: 'artefacts/bindings/bindings.yaml', sha256: textSha(before) }]);
    expect(releaseLedgerIssues(env.store.content!.files)).toEqual([]);
    // Any other seeded file is never replaced.
    const files = new Map(env.store.content!.files);
    const p = 'artefacts/p/pack/1.0.0.yaml';
    files.set(`change-sets/ledger/CS-2026-11-30-001.yaml`, `kind: release-ledger\nchangeSet: CS-2026-11-30-001\nfiles:\n  - { path: ${p}, sha256: ${textSha('x')} }\nreplaced:\n  - { path: ${p}, sha256: ${textSha(files.get(p)!)} }\n`);
    expect(releaseLedgerIssues(files).map((i) => i.message).join('\n')).toMatch(/publishes artefacts\/p\/pack\/1\.0\.0\.yaml again/);
  }, 60000);
});
