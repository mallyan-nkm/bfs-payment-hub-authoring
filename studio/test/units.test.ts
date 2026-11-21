import { spawn, spawnSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import * as readline from 'node:readline';
import { describe, expect, it } from 'vitest';
import { addDuration, DEFAULT_CALENDAR, durationMs, formatDuration, isDuration, parseDuration } from '../src/core/duration.js';
import { zip, unzip } from '../src/export/zip.js';
import { build, loadKit } from '../src/conformance/kit.js';
import { PartyService } from '../src/conformance/party.js';
import { SchemaValidator } from '../src/core/schema.js';
import { exportPackage, exportZip } from '../src/export/exporter.js';
import { deploy } from '../src/deploy/deploy.js';
import { freshEnv } from './helpers.js';

// White box: units the larger suites reach only through other processes, or not at all.

const env = freshEnv();
const T0 = Date.parse('2026-11-17T06:00:00Z'); // a Tuesday

describe('durations and the business calendar', () => {
  it('parses, formats and refuses', () => {
    expect(durationMs('PT1H30M')).toBe(5400000);
    expect(durationMs('P1DT1S')).toBe(86401000);
    expect(formatDuration(5400000)).toBe('PT1H30M');
    expect(formatDuration(0)).toMatch(/^PT0S$|^P0D$/);
    expect(isDuration('P2W')).toBe(true);
    expect(isDuration('two days')).toBe(false);
    expect(isDuration(7)).toBe(false);
    expect(() => parseDuration('P')).toThrow(/ISO 8601/);
    expect(() => parseDuration('PT')).toThrow(/ISO 8601/);
    expect(() => durationMs('P1M')).toThrow(/calendar parts/);
  });

  it('adds calendar months, and business days that skip the weekend and holidays', () => {
    expect(new Date(addDuration(T0, 'P1M')).toISOString().slice(0, 10)).toBe('2026-12-17');
    expect(new Date(addDuration(T0, 'P1Y')).toISOString().slice(0, 10)).toBe('2027-11-17');
    const fri = Date.parse('2026-11-20T06:00:00Z');
    expect(new Date(addDuration(fri, 'P1D', DEFAULT_CALENDAR)).toISOString().slice(0, 10)).toBe('2026-11-23'); // Monday
    expect(new Date(addDuration(fri, 'P1D', { weekend: [0, 6], holidays: ['2026-11-23'] })).toISOString().slice(0, 10)).toBe('2026-11-24');
    expect(addDuration(T0, 'P1W') - T0).toBe(7 * 86400000);
  });
});

describe('the reproducible zip', () => {
  it('round-trips names and bytes, and reads only stored entries', () => {
    const entries = [{ name: 'a.txt', content: Buffer.from('alpha') }, { name: 'dir/b.bin', content: Buffer.from([0, 1, 2, 255]) }, { name: 'empty', content: Buffer.alloc(0) }];
    const z = zip(entries);
    expect(zip(entries).equals(z)).toBe(true);
    const back = unzip(z);
    expect(back.map((e) => e.name)).toEqual(entries.map((e) => e.name));
    back.forEach((e, i) => expect(e.content.equals(entries[i].content)).toBe(true));
    const deflated = Buffer.from(z);
    deflated.writeUInt16LE(8, 8);
    expect(() => unzip(deflated)).toThrow(/only stored entries/);
    expect(unzip(Buffer.from('not a zip'))).toEqual([]);
  });
});

describe('schema validation', () => {
  it('names the kind that has no schema and the field that is wrong', () => {
    const v = new SchemaValidator(path.join(env.specRoot, 'schemas'));
    const none = v.validate({ kind: 'no-such-kind' } as any);
    expect(none[0].message).toContain('Unknown kind "no-such-kind"');
    const bad = v.validate({ kind: 'rule', name: 'p.rule.x.v1', release: 'one', title: 'x' } as any);
    expect(bad.length).toBeGreaterThan(0);
    expect(JSON.stringify(bad)).toMatch(/release|required/);
  });
});

describe('the party service', () => {
  const kit = loadKit(env);
  const party = (id: string) => {
    const sc = kit.scenarios.scenarios.find((s: any) => s.id === id);
    const exp = kit.manifest.scenarios.find((s: any) => s.id === id);
    const b = build(env, kit, sc, exp);
    return { b, p: PartyService.forScenario(env, b) };
  };
  const req = (b: any, activity: string, step: string, attempt = 1, now = T0) => ({ instance: 'i-1', activity, step, call: 1, attempt, now, ctx: b.context });

  it('fails twice and then accepts, as the simulator scenario says', async () => {
    const { b, p } = party('CK-RETRY-01');
    const r1 = await p.attempt(req(b, 'ck.kit.exchange.call.v1', 'a', 1));
    const r2 = await p.attempt(req(b, 'ck.kit.exchange.call.v1', 'a', 2));
    const r3 = await p.attempt(req(b, 'ck.kit.exchange.call.v1', 'a', 3));
    expect([r1.kind, r2.kind]).toEqual(['error', 'error']);
    expect(r3).toMatchObject({ kind: 'result', outcome: 'ok' });
    expect(p.sent().length).toBeGreaterThanOrEqual(1);
  });

  it('hands a later reply to the engine as a delivery, and keeps it for a map to read', async () => {
    const { b, p } = party('CK-AWAIT-01');
    const r = await p.attempt(req(b, 'ck.kit.exchange.send.v1', 'send'));
    expect(r.kind).toBe('result');
    const d = p.takeDeliveries();
    expect(d.map((x) => x.message.type)).toContain('ck.kit.reply');
    expect(d[0].at).toBeGreaterThanOrEqual(T0);
    expect(p.takeDeliveries()).toEqual([]);
    expect((p as any).run.messages.some((m: any) => m.id === d[0].message.id)).toBe(true);
  });

  it('registers payloads handed over with a request, and refuses an activity that does not resolve', async () => {
    const { b, p } = party('CK-SEQ-01');
    const r = await p.attempt({ ...req(b, 'ck.kit.nope.v1', 'x'), messages: [{ id: 'm-1', type: 't', payload: 'x' }] });
    expect(r).toMatchObject({ kind: 'error', name: 'NotFound' });
    expect((p as any).run.messages.some((m: any) => m.id === 'm-1')).toBe(true);
    expect(p.start(T0)).toEqual([]);
  });

  it('speaks JSON lines to an engine harness in another process', async () => {
    const child = spawn(path.join(process.cwd(), 'node_modules', '.bin', 'tsx'), ['src/conformance/party-server.ts'], { stdio: ['pipe', 'pipe', 'inherit'] });
    const lines = readline.createInterface({ input: child.stdout });
    const it = lines[Symbol.asyncIterator]();
    const ask = async (o: any) => {
      child.stdin.write(JSON.stringify(o) + '\n');
      return JSON.parse((await it.next()).value);
    };
    expect((await ask({ op: 'start', now: T0 })).error).toContain('open a scenario first');
    expect((await ask({ op: 'open', scenario: 'CK-NOPE' })).error).toContain('No scenario');
    const opened = await ask({ op: 'open', scenario: 'CK-SEQ-01' });
    expect(opened.ok).toBe(true);
    expect((await ask({ op: 'start', now: T0 })).deliveries).toEqual([]);
    const a = await ask({ op: 'attempt', instance: 'i', activity: 'ck.kit.enrich.task.v1', step: 'a', call: 1, attempt: 1, now: T0, ctx: opened.built.context });
    expect(a.kind).toBe('result');
    expect(Array.isArray((await ask({ op: 'sent' })).messages)).toBe(true);
    expect((await ask({ op: 'frobnicate' })).error).toContain('Unknown op');
    expect((await ask('not json' as any)).error).toBeDefined();
    child.kill();
  }, 60000);
});

const dotnet = spawnSync('dotnet', ['--version']).status === 0;
describe.skipIf(!dotnet)('the deploy runner, in process', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-deploy-'));
  const zipPath = path.join(dir, 'p.zip');
  fs.writeFileSync(zipPath, exportZip(exportPackage(env, { workflows: ['uae.wps.wf.salary-file-out.v1'], target: 'dtfx', withSimulators: true })));
  const inbound = { payload: fs.readFileSync(path.join(env.studioRoot, 'fixtures', 'samples', 'wps-sif-valid.sif'), 'utf8') };
  const environment = (scenario: string) => ({ evaluationDate: '2026-11-17', bench: [{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario }] });

  it('starts a worker per queue, runs the leg on DTFx and stops the workers', async () => {
    const r = await deploy({ packagePath: zipPath, workflow: 'uae.wps.wf.salary-file-out.v1', studioRoot: env.studioRoot, environment: environment('no-reply'), inbound });
    expect(r.outcome).toBe('pending');
    expect(r.intake).toMatchObject({ activity: 'uae.wps.map.sif-in.v1', queue: 'th-uae-wps' });
    expect(r.workers.map((w) => w.queue)).toEqual(['th-uae-p', 'th-uae-wps']);
    expect(new Set(r.workers.map((w) => w.pid)).size).toBe(2);
    for (const w of r.workers) expect(() => process.kill(w.pid, 0)).toThrow(); // stopped
  }, 300000);

  it('refuses a workflow that is not in the package, and a package with no workers', async () => {
    await expect(deploy({ packagePath: zipPath, workflow: 'uae.wps.wf.not-in-package.v1', studioRoot: env.studioRoot, environment: environment('accept'), inbound })).rejects.toThrow();
    const empty = path.join(dir, 'empty');
    fs.mkdirSync(empty);
    await expect(deploy({ packagePath: empty, workflow: 'x', studioRoot: env.studioRoot, environment: environment('accept') })).rejects.toThrow();
  }, 120000);
});

import { anthropicDrafter, assistantConfigured, draft, MODEL } from '../src/ai/assistant.js';
import { testCoverageReport, untestedOutcomes } from '../src/testing/coverage.js';

describe('the AI assistant, with a fake client', () => {
  const fakeClient = (response: any) => {
    const calls: any[] = [];
    return { calls, client: { beta: { messages: { create: async (body: any) => (calls.push(body), response) } } } as any };
  };

  it('asks for structured output on the configured model, and reads the JSON it returns', async () => {
    const { calls, client } = fakeClient({ stop_reason: 'end_turn', model: MODEL, content: [{ type: 'text', text: '{"artefactYaml":"","questions":["Which month?"]}' }] });
    const out = await anthropicDrafter(client)('system', 'user');
    expect(calls[0].model).toBe(MODEL);
    expect(calls[0].output_config.format.type).toBe('json_schema');
    expect(out.json.questions).toEqual(['Which month?']);
  });

  it('says when the model declines, or returns nothing', async () => {
    const { client } = fakeClient({ stop_reason: 'refusal', model: MODEL, content: [] });
    await expect(draft(env, { kind: 'rule', instruction: 'x' } as any, anthropicDrafter(client))).rejects.toThrow('declined');
    await expect(draft(env, { kind: 'rule', instruction: 'x' } as any, async () => ({ json: undefined }))).rejects.toThrow('no draft');
  });

  it('drafts a script for an existing activity without touching its provenance, and gates it', async () => {
    const d = await draft(env, { kind: 'script', target: 'uae.p.validate.iban.v1', instruction: 'check the IBAN', language: 'typescript' } as any,
      async () => ({ json: { scriptSource: 'export default async function run(io) { return { outcome: Math.random() > 0.5 ? "pass" : "reject" }; }', questions: [], clauses: [] }, stopReason: 'end_turn', model: 'm' }));
    expect(d.script?.language).toBe('typescript');
    expect(d.doc.name).toBe('uae.p.validate.iban.v1');
    expect(d.gates.find((g) => g.gate === 'determinism')?.passed).toBe(false);
    expect(d.servedBy).toBe('m');
  });

  it('knows when it has credentials', () => {
    const saved = { ...process.env };
    try {
      delete process.env.ANTHROPIC_API_KEY; delete process.env.TH_ANTHROPIC_API_KEY; delete process.env.ANTHROPIC_AUTH_TOKEN; delete process.env.ANTHROPIC_PROFILE;
      process.env.HOME = fs.mkdtempSync(path.join(os.tmpdir(), 'th-home-'));
      expect(assistantConfigured().configured).toBe(false);
      fs.mkdirSync(path.join(process.env.HOME, '.config', 'anthropic'), { recursive: true });
      expect(assistantConfigured()).toEqual({ configured: true, how: 'ant profile' });
      process.env.ANTHROPIC_API_KEY = 'test-key-not-real';
      expect(assistantConfigured().how).toBe('environment');
    } finally {
      process.env = saved;
    }
  });
});

describe('test coverage of outcomes', () => {
  it('reports untested outcomes per activity, workflow and hook, and nothing for an unknown name', () => {
    expect(untestedOutcomes(env, 'nope.x.y.v1')).toBeUndefined();
    expect(untestedOutcomes(env, 'uae.wps.wf.salary-file-out.v1')!.untested).toEqual(['returned']); // Issue I-25
    const rows = testCoverageReport(env);
    expect(rows.some((r) => r.kind === 'hook')).toBe(true);
    expect(rows.some((r) => r.kind === 'workflow')).toBe(true);
    expect(rows.every((r) => r.untested.every((o: string) => r.outcomes.includes(o)))).toBe(true);
  });
});


describe('simulator conversation state (R-07-05)', () => {
  it('answers each new request of a simulator keyed by message id, not only the first', async () => {
    const { runTest } = await import('../src/testing/runner.js');
    const bench = ['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' }))
      .concat([{ port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' }, { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' }]);
    const r = await runTest(env, { kind: 'test', id: 'X-E2E', title: 'x', target: 'p.wf.corporate.v1', level: 'end-to-end', evaluationDate: '2026-11-17', tenant: 'example-bank', bench, given: { inbound: 'samples/wps-sif-valid.sif', message: 'uae.wps.sif' }, expect: { outcome: 'settled' } });
    // Reserve, post and verify each send core banking a request; each is answered.
    expect(r.trace).toEqual(expect.arrayContaining(['reserve:reserved', 'leg:settled', 'post:posted', 'verify:verified', 'end:settled']));
    expect(r.passed).toBe(true);
  });

  it('refuses a channel file named as a message that starts nothing', async () => {
    const { runTest } = await import('../src/testing/runner.js');
    const r = await runTest(env, { kind: 'test', id: 'X-E2E-2', title: 'x', target: 'p.wf.corporate.v1', level: 'end-to-end', evaluationDate: '2026-11-17', bench: [], given: { inbound: 'samples/wps-sif-valid.sif', message: 'uae.wps.ack' }, expect: { outcome: 'settled' } });
    expect(r.passed).toBe(false);
    expect(r.error).toContain('R-07-14');
  });
});
