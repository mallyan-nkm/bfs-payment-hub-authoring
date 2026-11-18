import Anthropic from '@anthropic-ai/sdk';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { parseYaml, toYaml } from '../core/yaml.js';
import type { StudioEnv } from '../env.js';
import { explainBench, explainDoc } from '../explain/explain.js';
import { fieldHelp } from '../explain/fields.js';
import { MODEL } from './assistant.js';

// The inline assistant (docs/ISSUES.md I-29). It sits beside every surface where something is built:
// the artefact editors, the workflow designer, a bank's configuration and the test bench. It sees what
// the designer sees: the surface, the document as it stands, its checks, the field or step in focus.
// It works through tools. Tools that only look run at once. Tools that change something (edit the
// document, undo its own change, save a draft, save a test) stop and ask the designer: allow once,
// allow always, or deny, as a coding agent asks. It never publishes: publishing stays with a checker.
//
// The server holds no conversation: the browser sends the history back each turn. The document is
// worked on as a copy; every change carries its before and after, so either side can undo it.

export type Surface = 'editor' | 'designer' | 'tenant' | 'bench' | 'general';

export interface AgentContext {
  surface: Surface;
  kind?: string;
  /** The document on the surface: an artefact, a configuration set, or the bench set-up. */
  doc?: any;
  /** The label of the field in focus. */
  focus?: string;
  /** The step selected on the canvas. */
  selected?: string;
  /** The last bench run, on the bench. */
  report?: any;
  tenant: string;
  actor: string;
}

export interface Change { id: string; tool: string; summary: string; before: any; after: any }

export interface Pending {
  toolUseId: string;
  tool: string;
  input: any;
  summary: string;
  preview: { changed?: string[]; check?: string; detail?: string };
  /** Tool results already produced in this turn, before the one that waits. */
  results: any[];
  index: number;
  /** The model that asked for the change, for provenance. */
  model?: string;
}

export interface AgentRequest {
  context: AgentContext;
  messages: any[];
  /** Tools the designer allowed always, for this surface. */
  allow?: string[];
  /** Changes made earlier in the conversation, so the assistant can undo them. */
  history?: Change[];
  pending?: Pending;
  decision?: 'once' | 'always' | 'deny';
}

export interface AgentResponse {
  messages: any[];
  doc: any;
  changes: Change[];
  undone: string[];
  pending?: Pending;
  allow: string[];
  reply: string;
  saved: string[];
  servedBy?: string;
  stopReason?: string;
}

export type AgentModel = (req: { system: string; messages: any[]; tools: any[] }) => Promise<{ content: any[]; stop_reason: string; model?: string }>;

/** Tools that change something. Each asks the designer first, unless allowed always. */
export const WRITE_TOOLS = ['edit_document', 'undo_change', 'save_draft', 'save_test', 'save_sample'];
const MAX_ROUNDS = 16;

const obj = (properties: Record<string, any>, required: string[] = []) => ({ type: 'object', additionalProperties: false, properties, required });

export const TOOLS = [
  { name: 'get_document', description: 'The document on the surface as it stands now, as YAML. Call it after an edit or an undo to see the result.', input_schema: obj({}) },
  { name: 'check_document', description: 'Run the studio\'s checks on the document as it stands: schema, static checks, the rule\'s examples, the tests that target it, or a configuration\'s bounds. Returns each check, pass or fail, with the reason.', input_schema: obj({}) },
  { name: 'explain_document', description: 'The document explained in plain words, with the checklist of what is left before it can be published.', input_schema: obj({}) },
  { name: 'field_help', description: 'What a field of the form means, its allowed values and an example. Give the field label as the designer sees it.', input_schema: obj({ field: { type: 'string' } }, ['field']) },
  { name: 'search_catalogue', description: 'Find published blocks by name, title or type: activities, rules, rule sets, tables, ports, simulators, workflows, hooks.', input_schema: obj({ query: { type: 'string' }, kind: { type: 'string', description: 'Optional: activity, rule, ruleset, table, port, simulator, workflow, hook, test' } }, ['query']) },
  { name: 'read_artefact', description: 'Read a published document by name, as YAML. Use it to copy a pattern or check a contract.', input_schema: obj({ name: { type: 'string' } }, ['name']) },
  { name: 'list_samples', description: 'The sample files the bench can start from.', input_schema: obj({}) },
  {
    name: 'run_bench',
    description: 'Run a published workflow, hook or activity on the test bench, against simulators, with a made-up context or a sample file. Changes nothing. Returns the outcome and the business trace.',
    input_schema: obj({
      target: { type: 'string' }, tenant: { type: 'string' }, level: { type: 'string' },
      bench: { type: 'array', items: obj({ port: { type: 'string' }, simulator: { type: 'string' }, scenario: { type: 'string' } }, ['port', 'simulator', 'scenario']) },
      context: { type: 'object', additionalProperties: true }, inbound: { type: 'string' }, message: { type: 'string' },
    }, ['target']),
  },
  {
    name: 'edit_document',
    description: 'Replace the document on the surface with a new version. Send the WHOLE document as YAML, not a fragment. The designer sees what changes and is asked to allow it. Returns the checks on the new version.',
    input_schema: obj({ yaml: { type: 'string', description: 'The whole new document, as YAML' }, summary: { type: 'string', description: 'One line: what this change does, in plain words' } }, ['yaml', 'summary']),
  },
  { name: 'undo_change', description: 'Undo one of your own earlier changes, by its id (c1, c2 and so on). Only possible while the document is still as that change left it.', input_schema: obj({ changeId: { type: 'string' } }, ['changeId']) },
  { name: 'save_draft', description: 'Save the document as a draft, as the designer. A draft is not published: a checker publishes it later. Only on the editors and the designer.', input_schema: obj({}) },
  {
    name: 'save_test',
    description: 'Save a test as a draft: kind test, an id, a title, a published target, a level, an evaluationDate, optional bench bindings, given (context, or inbound and message) and expect (outcome, and optionally reasonCode or trace). Run it with run_bench first.',
    input_schema: obj({ yaml: { type: 'string', description: 'The test document, as YAML' } }, ['yaml']),
  },
  { name: 'save_sample', description: 'Save a synthetic sample file for the bench. Never personal data.', input_schema: obj({ name: { type: 'string' }, content: { type: 'string' } }, ['name', 'content']) },
];

const SURFACE_TEXT: Record<Surface, string> = {
  editor: 'the artefact editor. The document is one artefact: a rule, decision table, activity, map, simulator, composite, rule set, port or scheme.',
  designer: 'the workflow designer. The document is a workflow (payment, leg, segment, subflow or handler) or a bank\'s hook.',
  tenant: 'a bank\'s configuration page. The document is the bank\'s configuration set: the shared releases it accepts, the values it sets, and the hooks it binds, each from a date. Saving it is the bank maker\'s action on the page; you edit the form only.',
  bench: 'the test bench. The document is the bench set-up: target, tenant, port bindings, input and expectation. run_bench runs a set-up; save_test keeps a run as a test.',
  general: 'a page with nothing being edited.',
};

function readSpec(env: StudioEnv, rel: string): string {
  const f = path.join(env.specRoot, rel);
  return fs.existsSync(f) ? fs.readFileSync(f, 'utf8') : '';
}

const SCHEMA_FILE: Record<string, string> = { rule: 'rule', ruleset: 'rule', table: 'rule', activity: 'activity', simulator: 'simulator', port: 'port', scheme: 'scheme', workflow: 'workflow', hook: 'hook', 'configuration-set': 'configuration-set', test: 'test-case' };

/** The system prompt: stable parts first (guard-rails, the schema of the kind), then the surface. */
export function agentSystem(env: StudioEnv, ctx: AgentContext): string {
  const kind = ctx.doc?.kind ?? ctx.kind;
  const schema = kind && SCHEMA_FILE[kind] ? readSpec(env, `schemas/${SCHEMA_FILE[kind]}.schema.json`) : '';
  return [
    'You are the inline assistant of the TransactHub Designer Studio, a design-time tool for payment workflows over an ISO 20022 canonical context. You work beside a designer who may not be a payments engineer.',
    'Write British English, short sentences, plain words, no em-dashes. Explain payment terms when you use them.',
    'Work through the tools. Look before you change: get_document, check_document, search_catalogue and read_artefact are free. Every change goes through edit_document with the WHOLE document; the designer is asked to allow it. After a change, run check_document and fix what fails.',
    'To build a rule: write the When and Then, then examples: one that passes, one that fails, and one exactly at any boundary. Check that every example passes. To build tests for an activity or workflow: run it with run_bench for each outcome, then save_test for each run that shows what you meant.',
    'Never publish, and never claim to: publishing needs a checker who is not the maker. Never invent a scheme fact: where you do not know a rule of the scheme, say so and ask. Use only synthetic values; never personal data, bank names or vendor names.',
    'A value a bank may change is a parameter with a default and bounds, never a constant. Every non-primary outcome of a step must be handled. YAML keys on, off, yes and no are not allowed.',
    'When you finish, say in two or three sentences what you did and what the designer should do next.',
    '<guard-rails>', readSpec(env, 'CLAUDE.md'), '</guard-rails>',
    schema ? `<schema kind="${kind}">\n${schema}\n</schema>` : '',
    `<surface>The designer is on ${SURFACE_TEXT[ctx.surface] ?? ctx.surface}</surface>`,
  ].join('\n');
}

/** What the designer is looking at, sent at the start of each turn. */
export function contextNote(ctx: AgentContext, history: Change[]): string {
  return [
    '<context>',
    `surface: ${ctx.surface}; tenant: ${ctx.tenant}; acting as: ${ctx.actor}`,
    ctx.focus ? `field in focus: ${ctx.focus}` : '',
    ctx.selected ? `step selected on the canvas: ${ctx.selected}` : '',
    history.length ? `your earlier changes, newest last: ${history.map((c) => `${c.id} ${c.summary}`).join('; ')}` : '',
    ctx.doc ? `document now:\n${toYaml(ctx.doc)}` : 'no document',
    ctx.report ? `last bench run: outcome ${ctx.report.outcome}; trace ${(ctx.report.trace ?? []).join(' | ')}` : '',
    '</context>',
  ].filter(Boolean).join('\n');
}

/** The live model, with the server's credentials. TH_ANTHROPIC_API_KEY is read as well, for hosts that reserve ANTHROPIC_API_KEY. */
export function anthropicAgentModel(client?: Anthropic): AgentModel {
  const c = client ?? new Anthropic(process.env.TH_ANTHROPIC_API_KEY && !process.env.ANTHROPIC_API_KEY ? { apiKey: process.env.TH_ANTHROPIC_API_KEY } : {});
  return async ({ system, messages, tools }) => {
    const r = await c.messages.create({ model: MODEL, max_tokens: 16000, system, messages, tools } as any);
    return { content: r.content as any[], stop_reason: r.stop_reason ?? 'end_turn', model: r.model };
  };
}

/** A model that replays recorded turns, for tests and offline demonstrations. Its answers say so. */
export function replayModel(turns: { content: any[]; stop_reason?: string }[]): AgentModel {
  // The turn is chosen by how many answers the conversation already holds, so a replay needs no
  // state on the server and survives the pauses where the designer is asked.
  return async ({ messages }) => {
    const t = turns[Math.min(messages.filter((m) => m.role === 'assistant').length, turns.length - 1)];
    return { content: t.content, stop_reason: t.stop_reason ?? (t.content.some((b) => b.type === 'tool_use') ? 'tool_use' : 'end_turn'), model: 'replay' };
  };
}

// ------------------------------------------------------------------------ tools

interface State {
  env: StudioEnv;
  ctx: AgentContext;
  doc: any;
  history: Change[];
  changes: Change[];
  undone: string[];
  saved: string[];
  model?: string;
  dispatch: (method: string, p: string, body?: any) => Promise<{ status: number; body: any }>;
}

const same = (a: any, b: any) => JSON.stringify(a) === JSON.stringify(b);

function changedKeys(before: any, after: any): string[] {
  const keys = new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})]);
  return [...keys].filter((k) => !same(before?.[k], after?.[k]));
}

async function checkText(s: State, doc: any): Promise<string> {
  if (!doc) return 'There is no document.';
  if (s.ctx.surface === 'tenant' || doc.kind === 'configuration-set') {
    const r = await s.dispatch('POST', `/api/tenants/${encodeURIComponent(s.ctx.tenant)}/configuration/validate`, { doc });
    const errs = (r.body.issues ?? []).filter((i: any) => i.severity === 'error');
    return errs.length ? `Refused: ${errs.map((i: any) => `${i.code} ${i.message}`).join('; ')}` : 'The configuration passes every check.';
  }
  if (s.ctx.surface === 'bench') {
    const e = explainBench(s.env, doc);
    return e.checklist.map((c) => `${c.ok ? 'ok' : 'NOT YET'}: ${c.text}`).join('\n');
  }
  const r = await s.dispatch('POST', '/api/gates', { doc, tenant: s.ctx.tenant });
  return (r.body.gates ?? []).map((g: any) => `${g.gate}: ${g.passed ? 'pass' : 'FAIL'}${g.passed ? '' : ` ${JSON.stringify(g.detail).slice(0, 1500)}`}`).join('\n');
}

function parseDoc(yaml: string): any {
  const d = parseYaml(yaml);
  if (!d || typeof d !== 'object' || Array.isArray(d)) throw new Error('The YAML is not one document.');
  return d;
}

/** Stamp what the assistant did in provenance (C16-S5): the model and a reference to the prompt. */
function stamp(s: State, doc: any): any {
  if (!doc?.kind || ['configuration-set', 'test', 'scheme'].includes(doc.kind) || s.ctx.surface === 'bench') return doc;
  const prov = { origin: 'authored', status: 'provisional', ...(doc.provenance ?? {}) };
  const promptRef = `inline-assistant:${createHash('sha256').update(agentSystem(s.env, s.ctx)).digest('hex').slice(0, 16)}`;
  prov.ai = { model: s.model ?? MODEL, promptRef, inputs: [...new Set([...(prov.ai?.inputs ?? [])])] };
  return { ...doc, provenance: prov };
}

/** What a write would do, shown to the designer before they allow it. */
async function preview(s: State, tool: string, input: any): Promise<{ summary: string; preview: Pending['preview'] }> {
  switch (tool) {
    case 'edit_document': {
      let next: any;
      try {
        next = parseDoc(input.yaml);
      } catch (e: any) {
        return { summary: input.summary ?? 'Edit the document', preview: { detail: `The YAML does not parse: ${e.message}` } };
      }
      return { summary: input.summary ?? 'Edit the document', preview: { changed: changedKeys(s.doc, next), check: await checkText(s, next), detail: toYaml(next) } };
    }
    case 'undo_change': {
      const c = [...s.history, ...s.changes].find((x) => x.id === input.changeId);
      return { summary: `Undo ${input.changeId}${c ? `: ${c.summary}` : ''}`, preview: { changed: c ? changedKeys(c.after, c.before) : [] } };
    }
    case 'save_draft': return { summary: `Save ${s.doc?.name ?? s.doc?.kind ?? 'the document'} as a draft, as ${s.ctx.actor}`, preview: { check: await checkText(s, s.doc) } };
    case 'save_test': return { summary: 'Save a test as a draft', preview: { detail: input.yaml } };
    case 'save_sample': return { summary: `Save the sample file ${input.name}`, preview: { detail: String(input.content ?? '').slice(0, 2000) } };
    default: return { summary: tool, preview: {} };
  }
}

async function runTool(s: State, name: string, input: any): Promise<string> {
  switch (name) {
    case 'get_document': return s.doc ? toYaml(s.doc) : 'There is no document on this surface.';
    case 'check_document': return checkText(s, s.doc);
    case 'explain_document': {
      const e = s.ctx.surface === 'bench' ? explainBench(s.env, s.doc, s.ctx.report) : explainDoc(s.env, s.doc, {});
      return [e.title, ...e.lines, 'Checklist:', ...e.checklist.map((c) => `${c.ok ? 'ok' : 'NOT YET'}: ${c.text}`), e.next ?? ''].join('\n');
    }
    case 'field_help': {
      const h = fieldHelp(input.field);
      return h ? JSON.stringify(h) : `No help for "${input.field}".`;
    }
    case 'search_catalogue': {
      const q = String(input.query ?? '').toLowerCase();
      const hits = s.env.store.all()
        .filter((e) => e.status === 'published' && (e.tenant === 'owner' || e.tenant === s.ctx.tenant) && (!input.kind || e.kind === input.kind))
        .filter((e) => `${e.name} ${e.doc?.title ?? ''} ${e.doc?.type ?? ''} ${e.doc?.wfKind ?? ''}`.toLowerCase().includes(q))
        .slice(0, 20);
      return hits.length ? hits.map((e) => `${e.name}@${e.release} (${e.kind}${e.doc?.type ? ` ${e.doc.type}` : ''}${e.doc?.wfKind ? ` ${e.doc.wfKind}` : ''}): ${e.doc?.title ?? ''}`).join('\n') : 'No match.';
    }
    case 'read_artefact': {
      const d = s.env.store.doc(input.name, undefined, s.ctx.tenant);
      return d ? toYaml(d) : `No published ${input.name}.`;
    }
    case 'list_samples': {
      const r = await s.dispatch('GET', '/api/samples');
      return JSON.stringify(r.body);
    }
    case 'run_bench': {
      const r = await s.dispatch('POST', '/api/bench/run', { ...input, tenant: input.tenant ?? s.ctx.tenant });
      if (r.status !== 200) return `The bench refused: ${r.body.error}`;
      return `outcome: ${r.body.outcome}${r.body.reasonCode ? `; reason ${r.body.reasonCode}` : ''}\ntrace: ${(r.body.trace ?? []).join(' | ')}${r.body.error ? `\nerror: ${String(r.body.error).split('\n')[0]}` : ''}`;
    }
    case 'edit_document': {
      let next = parseDoc(input.yaml);
      if (s.doc?.kind && next.kind && next.kind !== s.doc.kind && s.ctx.surface !== 'bench') return `Refused: this surface edits a ${s.doc.kind}, not a ${next.kind}.`;
      next = stamp(s, next);
      const c: Change = { id: `c${s.history.length + s.changes.length + 1}`, tool: name, summary: input.summary ?? 'An edit', before: s.doc, after: next };
      s.changes.push(c);
      s.doc = next;
      return `Applied as ${c.id}.\n${await checkText(s, next)}`;
    }
    case 'undo_change': {
      const c = [...s.history, ...s.changes].find((x) => x.id === input.changeId);
      if (!c) return `There is no change ${input.changeId}.`;
      if (!same(s.doc, c.after)) return `Refused: the document has changed since ${c.id}. Undo the later changes first, or edit it back.`;
      s.doc = c.before;
      s.undone.push(c.id);
      return `Undid ${c.id}. ${await checkText(s, s.doc)}`;
    }
    case 'save_draft': {
      if (!['editor', 'designer'].includes(s.ctx.surface)) return 'Refused: drafts are saved from the editors and the designer.';
      const r = await s.dispatch('POST', '/api/drafts', { doc: s.doc, actor: s.ctx.actor, tenant: s.ctx.tenant });
      if (r.status !== 200) return `Not saved: ${r.body.error}`;
      s.saved.push(r.body.draft.key);
      const errs = (r.body.issues ?? []).filter((i: any) => i.severity === 'error');
      return `Saved the draft ${r.body.draft.key}. ${errs.length ? `It still has errors: ${errs.map((i: any) => i.message).join('; ')}` : 'No errors.'} A checker who is not ${s.ctx.actor} publishes it.`;
    }
    case 'save_test': {
      const t = parseDoc(input.yaml);
      if (t.kind !== 'test') return 'Refused: a test has kind test.';
      const r = await s.dispatch('POST', '/api/drafts', { doc: t, actor: s.ctx.actor, tenant: s.ctx.tenant });
      if (r.status !== 200) return `Not saved: ${r.body.error}`;
      s.saved.push(r.body.draft.key);
      const errs = (r.body.issues ?? []).filter((i: any) => i.severity === 'error');
      return `Saved the test draft ${r.body.draft.key}.${errs.length ? ` Errors: ${errs.map((i: any) => i.message).join('; ')}` : ''}`;
    }
    case 'save_sample': {
      const r = await s.dispatch('POST', '/api/samples', { name: input.name, content: input.content, actor: s.ctx.actor, tenant: s.ctx.tenant });
      if (r.status !== 200) return `Not saved: ${r.body.error}`;
      s.saved.push(`samples/${input.name}`);
      return `Saved samples/${input.name}.`;
    }
    default: return `There is no tool ${name}.`;
  }
}

async function execute(s: State, block: any): Promise<any> {
  try {
    return { type: 'tool_result', tool_use_id: block.id, content: await runTool(s, block.name, block.input ?? {}) };
  } catch (e: any) {
    return { type: 'tool_result', tool_use_id: block.id, content: `Error: ${e.message}`, is_error: true };
  }
}

/**
 * One turn of the conversation: the designer's message (already the last of `messages`), or the
 * designer's answer to a pending change. Runs until the model ends its turn, or a change waits for
 * the designer.
 */
export async function agentTurn(env: StudioEnv, req: AgentRequest, model: AgentModel, dispatch: State['dispatch']): Promise<AgentResponse> {
  const allow = new Set(req.allow ?? []);
  const s: State = { env, ctx: req.context, doc: req.context.doc, history: req.history ?? [], changes: [], undone: [], saved: [], dispatch };
  const messages = [...(req.messages ?? [])];
  const system = agentSystem(env, req.context);
  let servedBy: string | undefined;
  let stopReason: string | undefined;

  // Finish a turn's tool calls, from `index`, with the results so far. Returns a pending change, or
  // undefined once every tool call of the turn has a result (pushed as the next user message).
  const finish = async (blocks: any[], results: any[], index: number): Promise<Pending | undefined> => {
    for (let i = index; i < blocks.length; i++) {
      const b = blocks[i];
      if (WRITE_TOOLS.includes(b.name) && !allow.has(b.name)) {
        const p = await preview(s, b.name, b.input ?? {});
        return { toolUseId: b.id, tool: b.name, input: b.input, summary: p.summary, preview: p.preview, results, index: i, model: s.model };
      }
      results.push(await execute(s, b));
    }
    messages.push({ role: 'user', content: results });
    return undefined;
  };

  const last = messages[messages.length - 1];
  if (req.pending) {
    // The designer answered a change that was waiting.
    const assistant = messages[messages.length - 1];
    const blocks = (assistant?.content ?? []).filter((b: any) => b.type === 'tool_use');
    const p = req.pending;
    s.model = p.model;
    const b = blocks[p.index];
    if (!b || b.id !== p.toolUseId) throw new Error('The pending change does not match the conversation.');
    const results = [...p.results];
    if (req.decision === 'deny') results.push({ type: 'tool_result', tool_use_id: b.id, content: 'The designer did not allow this change. Ask what they want instead, or do something else.', is_error: true });
    else {
      if (req.decision === 'always') allow.add(b.name);
      results.push(await execute(s, b));
    }
    const again = await finish(blocks, results, p.index + 1);
    if (again) return { messages, doc: s.doc, changes: s.changes, undone: s.undone, pending: again, allow: [...allow], reply: '', saved: s.saved };
  } else if (last?.role === 'user' && typeof last.content === 'string') {
    // Put what the designer is looking at in front of their words.
    messages[messages.length - 1] = { role: 'user', content: [{ type: 'text', text: contextNote(req.context, s.history) }, { type: 'text', text: last.content }] };
  }

  for (let round = 0; round < MAX_ROUNDS; round++) {
    const r = await model({ system, messages, tools: TOOLS });
    servedBy = r.model;
    s.model = r.model;
    stopReason = r.stop_reason;
    messages.push({ role: 'assistant', content: r.content });
    const blocks = r.content.filter((b: any) => b.type === 'tool_use');
    if (r.stop_reason !== 'tool_use' || !blocks.length) break;
    const p = await finish(blocks, [], 0);
    if (p) return { messages, doc: s.doc, changes: s.changes, undone: s.undone, pending: p, allow: [...allow], reply: textOf(r.content), saved: s.saved, servedBy };
  }
  const final = messages[messages.length - 1];
  return { messages, doc: s.doc, changes: s.changes, undone: s.undone, allow: [...allow], reply: final?.role === 'assistant' ? textOf(final.content) : '', saved: s.saved, servedBy, stopReason };
}

const textOf = (content: any[]) => (content ?? []).filter((b: any) => b.type === 'text').map((b: any) => b.text).join('\n').trim();
