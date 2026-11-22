import * as fs from 'node:fs';
import * as path from 'node:path';
import { describe, expect, it } from 'vitest';
import { toYaml } from '../src/core/yaml.js';
import { agentTurn, replayModel, WRITE_TOOLS, type AgentRequest } from '../src/ai/agent.js';
import { explainBench, explainDoc } from '../src/explain/explain.js';
import { FIELD_HELP, NO_HELP, fieldHelp } from '../src/explain/fields.js';
import { dispatchAs as dispatch } from './support/as-user.js';
import { freshEnv } from './helpers.js';

// The inline assistant and the "What is happening" panel (docs/ISSUES.md I-29).

const WEB = path.join(__dirname, '..', 'web', 'src', 'views');
const SURFACES = ['Editors.tsx', 'Canvas.tsx', 'Designer.tsx', 'Tenant.tsx', 'TestBench.tsx', 'Debugger.tsx'];

describe('field help', () => {
  it('every field on every authoring surface has help, or is an action', () => {
    const labels = new Set<string>();
    for (const f of SURFACES) {
      const src = fs.readFileSync(path.join(WEB, f), 'utf8');
      for (const m of src.matchAll(/(?:aria-)?label="([^"]+)"/g)) labels.add(m[1]);
      for (const m of src.matchAll(/label=\{`([^`$]+)\$\{/g)) labels.add(m[1]);
    }
    const missing = [...labels].filter((l) => !fieldHelp(l) && !NO_HELP.some((n) => l === n || (n.endsWith(' ') && l.startsWith(n))));
    expect(missing).toEqual([]);
    expect(labels.size).toBeGreaterThan(100);
  });
  it('matches a field named after its example by prefix', () => {
    expect(fieldHelp('Given of boundary, December')?.means).toMatch(/made-up facts/);
    expect(fieldHelp('Rule level')?.values?.map((v) => v[0])).toEqual(['payment', 'batch', 'item']);
  });
  it('is written without em-dashes', () => {
    expect(JSON.stringify(FIELD_HELP)).not.toMatch(/—/);
  });
});

describe('what is happening', () => {
  const env = freshEnv();
  it('explains every published kind in plain words', () => {
    const seen = new Set<string>();
    for (const e of env.store.all()) {
      if (e.status !== 'published' || seen.has(e.kind)) continue;
      seen.add(e.kind);
      const x = explainDoc(env, e.doc, {});
      expect(x.lines.length, e.name).toBeGreaterThan(0);
      expect(x.lines.join(' '), e.name).not.toMatch(/undefined|\[object Object\]/);
    }
    for (const k of ['rule', 'ruleset', 'activity', 'simulator', 'port', 'workflow', 'hook', 'configuration-set', 'test']) expect(seen.has(k), k).toBe(true);
  });
  it('a rule: says what it looks for, what it does, and what is left', () => {
    const rule = env.store.doc('uae.wps.rule.employer-id-format.v1');
    const x = explainDoc(env, rule, { gates: [{ gate: 'tests', passed: true, detail: [{ example: 'passes', passed: true }, { example: 'fails', passed: true }] }] });
    expect(x.lines.join(' ')).toMatch(/finds WpsBatch, calls it w, where not matches/);
    expect(x.lines.join(' ')).toMatch(/rejects ext\.uae\.wps\.employerId with reason code FF01/);
    expect(x.lines.join(' ')).toMatch(/2 of 2 pass/);
    expect(x.picture?.map((n) => n.label)).toEqual(['WpsBatch', 'When', 'reject']);
    const broken = { ...rule, examples: [rule.examples[0]] };
    const y = explainDoc(env, broken, {});
    expect(y.checklist.find((c) => /passing example and a failing/.test(c.text))?.ok).toBe(false);
    expect(y.next).toMatch(/^Next:/);
  });
  it('a workflow: tells the steps as a story, with what each outcome does', () => {
    const x = explainDoc(env, env.store.doc('uae.wps.wf.salary-file-out.v1'), { selected: 'file-checks' });
    const t = x.lines.join('\n');
    expect(t).toMatch(/fills the scheme-leg slot of p\.wf\.corporate\.v1/);
    expect(t).toMatch(/3\. file-checks: uses .* if repair, end as rejected; if reject, end as rejected\./);
    expect(t).toMatch(/For each line of the file, stopping at the first that fails:/);
    expect(t).toMatch(/The selected step, file-checks/);
  });
  it('the bench: what will run, and the last run step by step', () => {
    const x = explainBench(env, { target: 'p.wf.corporate.v1', tenant: 'example-bank', bench: [{ port: 'p.port.fraud.v1', simulator: 'p.sim.fraud.v1', scenario: 'accept' }] }, { outcome: 'rejected', trace: ['validate:pass', 'hook:hold', 'hold:declined'] });
    expect(x.lines.join(' ')).toMatch(/ended as rejected/);
    expect(x.picture?.find((n) => n.label === 'hold')?.tone).toBe('bad');
  });
  it('is served at /api/explain, with the gates run on the server', async () => {
    const env2 = freshEnv();
    const rule = env2.store.doc('uae.wps.rule.employer-id-format.v1');
    const r = await dispatch(env2, { method: 'POST', path: '/api/explain', query: {}, body: { surface: 'editor', doc: rule } });
    expect(r.status).toBe(200);
    expect(r.body.checklist.every((c: any) => c.ok)).toBe(true);
    expect(r.body.lines.join(' ')).toMatch(/2 of 2 pass/);
    const h = await dispatch(env2, { method: 'GET', path: '/api/help/fields', query: { label: 'Effect' }, body: {} });
    expect(h.body.values.map((v: any) => v[0])).toEqual(['pure', 'read', 'write', 'external']);
  });
});

// ------------------------------------------------------------------------ the agent

const tool = (id: string, name: string, input: any = {}) => ({ type: 'tool_use', id, name, input });
const text = (t: string) => ({ type: 'text', text: t });
const RULE = {
  kind: 'rule', name: 'uae.wps.rule.days-in-period.v1', release: '1.0.0', title: 'Days in the pay period are 1 to 31', ruleKind: 'field', level: 'item', repeatability: 'non-repeatable',
  when: [{ match: { fact: 'WpsItem', as: 'i', where: 'i.daysInPeriod < 1 or i.daysInPeriod > 31' } }],
  then: [{ yield: { finding: { severity: 'reject', path: 'items[].ext.uae.wps.daysInPeriod', reasonCode: 'DT01' } } }],
  examples: [
    { name: 'passes', evaluationDate: '2026-11-17', given: { WpsItem: { daysInPeriod: 30 } }, expect: { findings: [] } },
    { name: 'fails, zero', evaluationDate: '2026-11-17', given: { WpsItem: { daysInPeriod: 0 } }, expect: { findings: [{ reasonCode: 'DT01' }] } },
    { name: 'boundary, 31 passes', evaluationDate: '2026-11-17', given: { WpsItem: { daysInPeriod: 31 } }, expect: { findings: [] } },
  ],
  provenance: { origin: 'authored', status: 'provisional', clauses: ['Synthetic training extract'] },
};
const STARTER = { kind: 'rule', name: 'uae.wps.rule.new.v1', release: '1.0.0', title: 'New rule', ruleKind: 'field', when: [], then: [], examples: [] };

function harness(turns: any[]) {
  const env = freshEnv();
  const model = replayModel(turns);
  const inner = (method: string, p: string, body: any = {}) => dispatch(env, { method, path: p, query: { tenant: 'owner' }, body: { tenant: 'owner', ...body } });
  const turn = (req: Partial<AgentRequest>) => agentTurn(env, { context: { surface: 'editor', kind: 'rule', doc: STARTER, tenant: 'owner', actor: 'shared.designer' }, messages: [], ...req } as AgentRequest, model, inner);
  return { env, turn };
}

describe('the inline agent', () => {
  it('looks freely, then asks before it changes the document; allow once applies it', async () => {
    const { turn } = harness([
      { content: [tool('t1', 'get_document'), tool('t2', 'search_catalogue', { query: 'salary', kind: 'rule' })] },
      { content: [text('I will write the rule.'), tool('t3', 'edit_document', { yaml: toYaml(RULE), summary: 'Write the days-in-period rule with three examples' })] },
      { content: [tool('t4', 'check_document')] },
      { content: [text('Done: the rule and its three examples pass. Save it as a draft; a checker publishes it.')] },
    ]);
    const a = await turn({ messages: [{ role: 'user', content: 'Build a rule that days in the period are 1 to 31, with tests.' }] });
    expect(a.pending?.tool).toBe('edit_document');
    expect(a.pending?.summary).toMatch(/three examples/);
    expect(a.pending?.preview.changed).toEqual(expect.arrayContaining(['name', 'when', 'then', 'examples']));
    expect(a.pending?.preview.check).toMatch(/tests: pass/);
    expect(a.doc).toEqual(STARTER);
    // The free tools ran without asking, and the context went in front of the designer's words.
    const first = a.messages[0].content;
    expect(first[0].text).toMatch(/<context>[\s\S]*surface: editor/);
    expect(a.messages[2].content.map((r: any) => r.tool_use_id)).toEqual(['t1', 't2']);

    const b = await turn({ messages: a.messages, pending: a.pending, decision: 'once', context: { surface: 'editor', kind: 'rule', doc: a.doc, tenant: 'owner', actor: 'shared.designer' } });
    expect(b.pending).toBeUndefined();
    expect(b.doc.name).toBe(RULE.name);
    expect(b.changes.map((c) => c.id)).toEqual(['c1']);
    expect(b.changes[0].before).toEqual(STARTER);
    expect(b.doc.provenance.ai).toMatchObject({ model: 'replay', promptRef: expect.stringMatching(/^inline-assistant:/) });
    expect(b.allow).toEqual([]);
    expect(b.reply).toMatch(/checker publishes it/);
  });

  it('allow always: later changes of the same kind apply without asking', async () => {
    const v2 = { ...RULE, title: 'Days in the period are from 1 to 31' };
    const { turn } = harness([
      { content: [tool('t1', 'edit_document', { yaml: toYaml(RULE), summary: 'first' })] },
      { content: [tool('t2', 'edit_document', { yaml: toYaml(v2), summary: 'second' })] },
      { content: [text('Both done.')] },
    ]);
    const a = await turn({ messages: [{ role: 'user', content: 'go' }] });
    const b = await turn({ messages: a.messages, pending: a.pending, decision: 'always' });
    expect(b.pending).toBeUndefined();
    expect(b.allow).toEqual(['edit_document']);
    expect(b.changes.map((c) => c.summary)).toEqual(['first', 'second']);
    expect(b.doc.title).toBe(v2.title);
  });

  it('deny: nothing changes, and the model is told', async () => {
    const { turn } = harness([
      { content: [tool('t1', 'edit_document', { yaml: toYaml(RULE), summary: 'first' })] },
      { content: [text('Understood, I will not change it.')] },
    ]);
    const a = await turn({ messages: [{ role: 'user', content: 'go' }] });
    const b = await turn({ messages: a.messages, pending: a.pending, decision: 'deny' });
    expect(b.doc).toEqual(STARTER);
    expect(b.changes).toEqual([]);
    const result = b.messages.find((m) => m.role === 'user' && Array.isArray(m.content) && m.content[0]?.tool_use_id === 't1');
    expect(result.content[0]).toMatchObject({ is_error: true, content: expect.stringMatching(/did not allow/) });
  });

  it('undoes its own change, and refuses once the document moved on', async () => {
    const { turn } = harness([
      { content: [tool('t1', 'undo_change', { changeId: 'c1' })] },
      { content: [text('Undone.')] },
    ]);
    const history = [{ id: 'c1', tool: 'edit_document', summary: 'first', before: STARTER, after: RULE }];
    const a = await turn({ messages: [{ role: 'user', content: 'undo that' }], history, allow: ['undo_change'], context: { surface: 'editor', kind: 'rule', doc: RULE, tenant: 'owner', actor: 'shared.designer' } });
    expect(a.doc).toEqual(STARTER);
    expect(a.undone).toEqual(['c1']);
    const moved = harness([{ content: [tool('t1', 'undo_change', { changeId: 'c1' })] }, { content: [text('x')] }]);
    const b = await moved.turn({ messages: [{ role: 'user', content: 'undo' }], history, allow: ['undo_change'], context: { surface: 'editor', kind: 'rule', doc: { ...RULE, title: 'changed by hand' }, tenant: 'owner', actor: 'shared.designer' } });
    expect(b.doc.title).toBe('changed by hand');
    expect(JSON.stringify(b.messages)).toMatch(/has changed since c1/);
  });

  it('saves a draft and tests, never publishes', async () => {
    const test = { kind: 'test', id: 'T-AI-IBAN-REJECT', release: '1.0.0', title: 'A bad IBAN is rejected', target: 'uae.p.validate.iban.v1', level: 'activity', evaluationDate: '2026-11-17', given: { context: { header: { paymentId: 'P-1', endToEndId: 'E-1', kind: 'payment', direction: 'outward', segment: 'corporate', scheme: 'uae.wps' }, payment: { creditorAccount: { iban: 'AE000000000000000000000' } } } }, expect: { outcome: 'reject' } };
    const { env, turn } = harness([
      { content: [tool('t1', 'save_draft'), tool('t2', 'run_bench', { target: 'uae.p.validate.iban.v1', context: test.given.context }), tool('t3', 'save_test', { yaml: toYaml(test) })] },
      { content: [text('Saved.')] },
    ]);
    const a = await turn({ messages: [{ role: 'user', content: 'save it and add a test' }], allow: ['save_draft', 'save_test'], context: { surface: 'editor', kind: 'rule', doc: RULE, tenant: 'owner', actor: 'shared.designer' } });
    expect(a.saved).toEqual([`${RULE.name}@1.0.0#draft`, 'T-AI-IBAN-REJECT@1.0.0#draft']);
    expect(JSON.stringify(a.messages)).toMatch(/outcome: reject/);
    expect(env.store.all().filter((e) => e.name === RULE.name).map((e) => e.status)).toEqual(['draft']);
  });

  it('refuses an edit that changes the kind of the document', async () => {
    const { turn } = harness([{ content: [tool('t1', 'edit_document', { yaml: 'kind: activity\nname: x', summary: 'x' })] }, { content: [text('ok')] }]);
    const a = await turn({ messages: [{ role: 'user', content: 'go' }], allow: ['edit_document'] });
    expect(a.doc).toEqual(STARTER);
    expect(JSON.stringify(a.messages)).toMatch(/edits a rule, not a activity/);
  });

  it('every write tool asks first', () => {
    expect(WRITE_TOOLS.sort()).toEqual(['edit_document', 'save_draft', 'save_sample', 'save_test', 'undo_change']);
  });

  it('the route refuses without credentials or a replay', async () => {
    const env = freshEnv();
    const keep = { k: process.env.ANTHROPIC_API_KEY, t: process.env.TH_ANTHROPIC_API_KEY, r: process.env.TH_AI_REPLAY };
    delete process.env.ANTHROPIC_API_KEY; delete process.env.TH_ANTHROPIC_API_KEY; delete process.env.TH_AI_REPLAY;
    try {
      const r = await dispatch(env, { method: 'POST', path: '/api/ai/agent', query: {}, body: { context: { surface: 'editor', doc: STARTER, actor: 'shared.designer' }, messages: [{ role: 'user', content: 'hi' }] } });
      if (!process.env.ANTHROPIC_AUTH_TOKEN && !process.env.ANTHROPIC_PROFILE) expect(r.status).toBe(503);
    } finally {
      if (keep.k) process.env.ANTHROPIC_API_KEY = keep.k;
      if (keep.t) process.env.TH_ANTHROPIC_API_KEY = keep.t;
      if (keep.r) process.env.TH_AI_REPLAY = keep.r;
    }
  });
});
