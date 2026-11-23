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
      // the agent's leg at 2.1.0: its steps that may run again park (R-12-26); at 2.2.0 it fills p.wf.inward.v2 (R-09-87)
      expect(env.store.get(`${n}.v2`)!.release, n).toBe(n === 'uae.wps.wf.agent-payment-in' ? '2.2.0' : '2.0.0');
    }
    // the posting that reads FX and charges is a new major line; payments on v1 keep it (R-12-23)
    expect(env.store.get('p.post.accounting.v1')!.release).toBe('1.1.0');
    expect(env.store.get('p.post.accounting.v2')!.release).toBe('2.0.0');
    // 1.2.0: the agent's refund cycle (I-133); 1.3.0: the employer's refund order is an outward refund order (R-12-24)
    expect(env.store.scheme('uae.wps').release).toBe('1.3.0');
    expect(env.store.scheme('uae.wps').messages.find((m: any) => m.id === 'uae.wps.refund-order').disposition.start).toMatchObject({ kind: 'refund', direction: 'outward', leg: 'uae.wps.wf.refund-out.v2' });
    expect(env.store.scheme('uae.wps').messages.find((m: any) => m.id === 'uae.wps.pif').disposition.start.leg).toBe('uae.wps.wf.agent-payment-in.v2');
  });

  it('moves the banks\' configuration sets and pins to the new pack releases', () => {
    const env = freshEnv({ banks: ['dib'] } as any);
    for (const t of ['example-bank', 'dib']) {
      expect(env.store.configurationSet(t).accepts, t).toEqual([{ scope: 'p', release: '1.8.0' }, { scope: 'uae.p', release: '1.8.0' }, { scope: 'uae.wps', release: '1.8.0' }]);
    }
    // The example bank's first configuration set and lock file stay as they were published.
    expect(body(disk('banks/example-bank/configuration-set.yaml')).release).toBe('1.0.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.1.0.yaml')).release).toBe('1.1.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.2.0.yaml')).release).toBe('1.2.0');
    expect(body(disk('banks/example-bank/packs/1.0.0.yaml')).release).toBe('1.0.0');
    expect(body(disk('banks/example-bank/packs/1.1.0.yaml')).release).toBe('1.1.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.3.0.yaml')).release).toBe('1.3.0');
    expect(body(disk('banks/example-bank/packs/1.2.0.yaml')).release).toBe('1.2.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.5.0.yaml')).release).toBe('1.5.0');
    expect(body(disk('banks/example-bank/packs/1.3.0.yaml')).release).toBe('1.3.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.6.0.yaml')).release).toBe('1.6.0');
    expect(body(disk('banks/example-bank/packs/1.5.0.yaml')).release).toBe('1.5.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.7.0.yaml')).release).toBe('1.7.0');
    expect(body(disk('banks/example-bank/packs/1.6.0.yaml')).release).toBe('1.6.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.8.0.yaml')).release).toBe('1.8.0');
    expect(body(disk('banks/example-bank/packs/1.7.0.yaml')).release).toBe('1.7.0');
    expect(body(disk('banks/example-bank/packs/1.8.0.yaml')).release).toBe('1.8.0');
    expect(body(disk('banks/example-bank/configuration-sets/1.9.0.yaml')).release).toBe('1.9.0');
    expect(body(disk('banks/example-bank/packs/1.9.0.yaml')).release).toBe('1.9.0');
    expect(body(disk('banks/example-bank/packs/1.10.0.yaml')).release).toBe('1.10.0');
    expect(body(disk('banks/example-bank/pack.yaml')).release).toBe('1.11.0');
    // Since 1.5.0 the lock file names the schemes the bank licenses (Issue I-111); the earlier one stays without.
    expect(body(disk('banks/example-bank/pack.yaml')).licensedSchemes).toEqual(['uae.wps']);
    expect(body(disk('banks/example-bank/packs/1.5.0.yaml')).licensedSchemes).toEqual(['uae.wps']);
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

  it('engine bindings are released like every other artefact: a published binding release is never replaced (Issue I-83)', async () => {
    const full = forkRepo();
    const P = `${ROOT}/artefacts/bindings/p.binding.temporal.v1/1.1.0.yaml`;
    const now = fake().files(full, BRANCH)[P];
    expect(now, 'the binding is published at its release').toBeDefined();
    expect(fake().files(full, BRANCH)[`${ROOT}/artefacts/bindings/p.binding.temporal.v1/1.0.0.yaml`]).not.toMatch(/forEachConcurrency/);
    expect(now).toMatch(/forEachConcurrency: 64/);
    // A binding changed on the branch at a release already published: the seed refuses to write it again.
    fake().commitFiles(full, BRANCH, { [P]: now.replace('forEachConcurrency: 64', 'forEachConcurrency: 32') }, 'A binding edited in place');
    const env = envFor(await ContentStore.open(connection(fake(), full)), { banks: [] });
    const plan = seedPlan(env);
    expect(plan.replace.map((r) => r.file.path)).toEqual([]);
    expect(plan.clash.map((c) => c.path)).toEqual([P.slice(ROOT.length + 1)]);
    // L41: a ledger that replaces a binding release is refused, and so is a later replacement of the old unreleased file.
    const files = new Map(env.store.content!.files);
    files.set(P.slice(ROOT.length + 1), now);
    const b = 'artefacts/bindings/p.binding.temporal.v1/1.1.0.yaml';
    files.set('change-sets/ledger/CS-2026-11-30-001.yaml', `kind: release-ledger\nchangeSet: CS-2026-11-30-001\nfiles:\n  - { path: ${b}, sha256: ${textSha('x')} }\nreplaced:\n  - { path: ${b}, sha256: ${textSha(now)} }\n`);
    expect(releaseLedgerIssues(files).map((i) => i.message).join('\n')).toMatch(/publishes artefacts\/bindings\/p\.binding\.temporal\.v1\/1\.1\.0\.yaml again/);
    const old = 'artefacts/bindings/bindings.yaml';
    files.set('change-sets/ledger/CS-2026-11-30-001.yaml', `kind: release-ledger\nchangeSet: CS-2026-11-30-001\nfiles:\n  - { path: ${old}, sha256: ${textSha('x')} }\nreplaced:\n  - { path: ${old}, sha256: ${textSha(files.get(old)!)} }\n`);
    expect(releaseLedgerIssues(files).map((i) => i.message).join('\n')).toMatch(/publishes artefacts\/bindings\/bindings\.yaml again/);
    // The studio reads the bindings at their latest release, and the old unreleased file is a record only.
    expect(env.store.byKind('engine-binding').every((e) => e.doc.name && e.doc.release)).toBe(true);
  }, 60000);
});
