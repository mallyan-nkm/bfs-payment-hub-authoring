import Anthropic from '@anthropic-ai/sdk';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { parseYaml, toYaml } from '../core/yaml.js';
import { runGates, type Gate } from './gates.js';
import type { StudioEnv } from '../env.js';

// The AI authoring assistant (Part 8; C16). It drafts a contract, rules, a script-behind or a
// simulator from scheme text and samples, with tests, and every draft goes through the same gates
// as a designer's. It never publishes: the designer saves a draft and maker-checker publishes.
// Provenance records the model, a reference to the prompt (its hash, not its text), the inputs
// and the clauses (C16-S5). Unknowns come back as questions, not guesses (C16-S2).

export const MODEL = 'claude-opus-5-5';

export type DraftKind = 'activity' | 'rule' | 'script' | 'simulator';

export interface DraftRequest {
  kind: DraftKind;
  instruction: string; // what the designer wants
  sourceText?: string; // scheme text: public rulebook extracts, guides
  target?: string; // for a script: the activity it implements; for a simulator: the port
  language?: 'typescript' | 'python';
  tenant?: string;
}

export interface Draft {
  yaml: string;
  doc: any;
  script?: { source: string; language: 'typescript' | 'python' };
  tests: any[];
  questions: string[];
  clauses: string[];
  gates: Gate[];
  passed: boolean;
  firstRed?: Gate;
  provenance: any;
  stopReason?: string;
  servedBy?: string;
}

/** What the model must return, as JSON (structured outputs). */
const SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['artefactYaml', 'scriptSource', 'testsYaml', 'questions', 'clauses'],
  properties: {
    artefactYaml: { type: 'string', description: 'The drafted artefact as one YAML document. Empty when drafting only a script.' },
    scriptSource: { type: 'string', description: 'The script-behind source, or empty' },
    testsYaml: { type: 'array', items: { type: 'string' }, description: 'Each a YAML test-case document' },
    questions: { type: 'array', items: { type: 'string' }, description: 'What the source does not say and was not guessed' },
    clauses: { type: 'array', items: { type: 'string' }, description: 'The clauses of the source each part comes from' },
  },
};

function read(env: StudioEnv, rel: string): string {
  const f = path.join(env.specRoot, rel);
  return fs.existsSync(f) ? fs.readFileSync(f, 'utf8') : '';
}

function example(env: StudioEnv, kind: string, prefer: string): string {
  const e = env.store.get(prefer) ?? env.store.byKind(kind)[0];
  return e ? toYaml(e.doc) : '';
}

/** The prompt. Stable parts first, so a cache can serve them; the request last. */
export function buildPrompt(env: StudioEnv, req: DraftRequest): { system: string; user: string; inputs: string[] } {
  const guard = read(env, 'CLAUDE.md');
  const schemaFile = { activity: 'activity', rule: 'rule', script: 'activity', simulator: 'simulator' }[req.kind];
  const schema = read(env, `schemas/${schemaFile}.schema.json`);
  const ex = { activity: example(env, 'activity', 'uae.p.validate.iban.v1'), rule: example(env, 'rule', 'uae.aani.rule.amount-within-limit.v1'), script: '', simulator: example(env, 'simulator', 'uae.p.sim.participant-directory.v1') }[req.kind];
  const inputs: string[] = [];
  let targetDoc = '';
  let scriptExample = '';
  if (req.target) {
    const t = env.store.doc(req.target);
    if (t) {
      targetDoc = toYaml(t);
      inputs.push(`${t.name}@${t.release}`);
    }
  }
  if (req.kind === 'script') {
    const f = path.join(env.studioRoot, 'scripts', 'p.validate.duplicate.v1.ts');
    if (fs.existsSync(f)) scriptExample = fs.readFileSync(f, 'utf8');
  }
  const system = [
    'You draft artefacts for the TransactHub Designer Studio, a design-time tool for payment workflows over an ISO 20022 canonical context.',
    'Follow the repository guard-rails below exactly. Write British English, short sentences, no em-dashes.',
    'Never invent a scheme fact. Where the source text is silent, list a question instead of guessing. Cite the clause of the source for each rule.',
    'Use only synthetic values in examples and tests. No personal data, no bank or vendor names.',
    'A rule ships at least one passing and one failing example, and a boundary example for any threshold. An activity gets one test per outcome.',
    'A script-behind uses only the injected interface: ctx, ports, rules, params, clock, ids, log. No network, file system, system clock, randomness or environment. It reads and writes only declared paths.',
    'Return JSON matching the given schema.',
    '<guard-rails>', guard, '</guard-rails>',
    `<schema kind="${schemaFile}">`, schema, '</schema>',
    ex ? `<example>\n${ex}\n</example>` : '',
  ].join('\n');
  const user = [
    `Draft: ${req.kind}${req.kind === 'script' ? ` in ${req.language ?? 'typescript'}` : ''}.`,
    `What the designer wants: ${req.instruction}`,
    targetDoc ? `<target>\n${targetDoc}\n</target>` : '',
    scriptExample ? `<script-example>\n${scriptExample}\n</script-example>` : '',
    req.sourceText ? `<source-text>\n${req.sourceText}\n</source-text>` : 'No source text was given: list what you would need as questions.',
  ].join('\n\n');
  return { system, user, inputs };
}

/** The model call. Injected so tests can run the loop and the gates without a live model. */
export type Drafter = (system: string, user: string) => Promise<{ json: any; stopReason?: string; model?: string }>;

export function anthropicDrafter(client = new Anthropic(process.env.TH_ANTHROPIC_API_KEY && !process.env.ANTHROPIC_API_KEY ? { apiKey: process.env.TH_ANTHROPIC_API_KEY } : {})): Drafter {
  return async (system, user) => {
    const response = await client.beta.messages.create({
      model: MODEL,
      max_tokens: 16000,
      betas: ['server-side-fallback-2026-07-01'],
      fallbacks: 'default',
      output_config: { effort: 'high', format: { type: 'json_schema', schema: SCHEMA } },
      system,
      messages: [{ role: 'user', content: user }],
    } as any);
    if (response.stop_reason === 'refusal') return { json: undefined, stopReason: 'refusal', model: response.model };
    const text = response.content.filter((b: any) => b.type === 'text').map((b: any) => b.text).join('');
    return { json: JSON.parse(text), stopReason: response.stop_reason ?? undefined, model: response.model };
  };
}

export async function draft(env: StudioEnv, req: DraftRequest, drafter: Drafter = anthropicDrafter()): Promise<Draft> {
  const { system, user, inputs } = buildPrompt(env, req);
  const out = await drafter(system, user);
  if (!out.json) throw new Error(out.stopReason === 'refusal' ? 'The model declined to draft this request' : 'The model returned no draft');
  const j = out.json;
  // Provenance as common#provenance holds it (R-08-11): the model, a prompt reference (a hash, so
  // no prompt text is stored) and the inputs under `ai`; the clauses beside them.
  const provenance = {
    origin: 'authored',
    status: 'provisional',
    clauses: j.clauses ?? [],
    ai: { model: out.model ?? MODEL, promptRef: 'sha256:' + createHash('sha256').update(system + '\n' + user, 'utf8').digest('hex'), inputs },
    note: 'Drafted by the AI assistant. Reviewed by behaviour through the gates; publish stays with maker-checker.',
  };
  let doc: any = {};
  if (j.artefactYaml?.trim()) doc = parseYaml(j.artefactYaml);
  else if (req.kind === 'script' && req.target) doc = env.store.doc(req.target) ?? {};
  if (req.kind !== 'script') doc.provenance = provenance;
  const tests = (j.testsYaml ?? []).map((t: string) => parseYaml(t)).filter((t: any) => t && t.kind === 'test');
  const script = j.scriptSource?.trim() ? { source: j.scriptSource, language: req.language ?? 'typescript' } as const : undefined;
  const g = await runGates(env, { doc, script, tests, tenant: req.tenant });
  return {
    yaml: toYaml(doc), doc, script, tests, questions: j.questions ?? [], clauses: j.clauses ?? [],
    gates: g.gates, passed: g.passed, firstRed: g.firstRed, provenance, stopReason: out.stopReason, servedBy: out.model,
  };
}

/** Whether the assistant can reach a model: an API key, an auth token, or an `ant` profile. */
export function assistantConfigured(): { configured: boolean; how: string } {
  const env = process.env;
  if (env.ANTHROPIC_API_KEY || env.TH_ANTHROPIC_API_KEY || env.ANTHROPIC_AUTH_TOKEN || env.ANTHROPIC_PROFILE) return { configured: true, how: 'environment' };
  const home = env.HOME ?? '';
  if (home && fs.existsSync(path.join(home, '.config', 'anthropic'))) return { configured: true, how: 'ant profile' };
  return { configured: false, how: 'Set ANTHROPIC_API_KEY (or TH_ANTHROPIC_API_KEY) for the studio server, or run `ant auth login` on its host.' };
}
