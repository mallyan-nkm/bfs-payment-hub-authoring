import type { StudioEnv } from '../env.js';

// "What is happening": a document, a bench set-up or a run, explained in plain words, with a small
// picture, a checklist of what is left before it can be published, and one next step. No model is
// involved, so it works without credentials and says the same thing every time (I-29).

export interface PictureNode { label: string; sub?: string; tone: 'in' | 'step' | 'ok' | 'bad' | 'out' | 'rule' }
export interface Check { ok: boolean; text: string }
export interface Explanation {
  title: string;
  lines: string[];
  picture?: PictureNode[];
  checklist: Check[];
  next?: string;
}
export interface GateLike { gate: string; passed: boolean; detail?: any }

const SEVERITY: Record<string, string> = { reject: 'rejects', repair: 'sends for repair', warn: 'warns about' };
const ACTIVITY_TYPE: Record<string, string> = {
  validate: 'checks the payment with a rule set', decide: 'takes a decision', enrich: 'adds a value to the payment', map: 'translates between a scheme format and the canonical model',
  exchange: 'talks to another party through a port', post: 'posts to accounts', screen: 'screens the payment', reserve: 'reserves funds', hold: 'parks the payment for a person',
  notify: 'tells someone what happened', reconcile: 'matches a report to the record', reverse: 'reverses a posting', verify: 'checks a posting really happened',
};
const EFFECT: Record<string, string> = { pure: 'only looks and changes nothing', read: 'reads from outside and changes nothing', write: 'changes something inside the bank', external: 'changes something at another party' };
const WF_KIND: Record<string, string> = {
  payment: 'a payment workflow: the whole journey of a payment', leg: 'a leg: the scheme\'s part of a payment, which fills a slot', segment: 'a segment: a shared piece used by several workflows',
  subflow: 'a subflow: what a composite block does underneath', handler: 'a handler: it deals with a message that arrives, and holds no state',
};

const list = (xs: string[]) => (xs.length <= 1 ? xs.join('') : `${xs.slice(0, -1).join(', ')} and ${xs[xs.length - 1]}`);
const gate = (gates: GateLike[] | undefined, name: string) => gates?.find((g) => g.gate === name);
const firstMessages = (g?: GateLike) => (Array.isArray(g?.detail) ? g!.detail.map((d: any) => d.message ?? d.problem ?? (d.example ? `example "${d.example}" ${d.passed ? 'passes' : 'fails'}` : d.id ? `test ${d.id} ${d.passed ? 'passes' : 'fails'}` : '')).filter(Boolean) : []);

function gateChecks(gates?: GateLike[]): Check[] {
  if (!gates) return [];
  const out: Check[] = [];
  const s = gate(gates, 'schema');
  if (s) out.push({ ok: s.passed, text: s.passed ? 'The document has the right shape.' : `The shape is wrong: ${firstMessages(s).slice(0, 2).join('; ')}` });
  const c = gate(gates, 'static checks');
  if (c) out.push({ ok: c.passed, text: c.passed ? 'Every static check passes.' : `A static check fails: ${firstMessages(c).slice(0, 2).join('; ')}` });
  const t = gate(gates, 'tests');
  if (t) {
    const bad = (Array.isArray(t.detail) ? t.detail : []).filter((d: any) => d.passed === false || d.problem);
    out.push({ ok: t.passed, text: t.passed ? 'Every example and test passes.' : `Not every example or test passes: ${bad.slice(0, 3).map((d: any) => d.problem ?? (d.example ? `"${d.example}" fails` : `${d.id} fails`)).join('; ')}` });
  }
  const p = gate(gates, 'provenance');
  if (p) out.push({ ok: p.passed, text: p.passed ? 'It says where it came from (provenance).' : 'It does not say where it came from: add provenance.' });
  return out;
}

function nextStep(checks: Check[], saved = 'Save it as a draft. A checker who is not you then publishes it.'): string {
  const bad = checks.find((c) => !c.ok);
  return bad ? `Next: ${bad.text.replace(/^Not /, 'Make ').replace(/:.*$/, '.')}` : saved;
}

// ---------------------------------------------------------------- rules

function condition(c: any): string {
  const k = Object.keys(c ?? {})[0];
  const v = c?.[k];
  switch (k) {
    case 'match': return `It finds ${v?.fact ?? 'a fact'}${v?.as ? `, calls it ${v.as},` : ''}${v?.where ? ` where ${v.where}` : ''}.`;
    case 'exists': return `There is a ${v?.fact ?? 'fact'}${v?.where ? ` where ${v.where}` : ''}.`;
    case 'not': return `There is no ${v?.fact ?? 'fact'}${v?.where ? ` where ${v.where}` : ''}.`;
    case 'all': return `Every ${v?.fact ?? 'fact'} meets ${v?.where ?? 'the condition'}.`;
    case 'having': return `And ${v}.`;
    case 'let': return `It works out ${Object.entries(v ?? {}).map(([n, e]) => `${n} as ${e}`).join(', ')}.`;
    case 'query': return 'It collects facts to count or add them.';
    case 'or': return 'One of several groups holds.';
    case 'and': return 'All of several groups hold.';
    default: return '';
  }
}

function explainRule(env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const lines: string[] = [];
  lines.push(`This is a ${doc.ruleKind ?? ''} rule at ${doc.level ?? 'payment'} level. It answers one question about the ${doc.level === 'batch' ? 'file' : doc.level === 'item' ? 'line of a file' : 'payment'}.`.replace(/ {2,}/g, ' '));
  for (const c of doc.when ?? []) { const s = condition(c); if (s) lines.push(s); }
  const params = Object.entries<any>(doc.parameters ?? {});
  for (const [k, p] of params) lines.push(`A bank may set ${k}: a ${p.type}, ${p.default !== undefined ? `${p.default} unless set` : 'with no default'}${p.bounds ? `, from ${p.bounds.min ?? 'any'} to ${p.bounds.max ?? 'any'}` : ''}.`);
  const then = (doc.then ?? []).map((t: any) => t?.yield).filter(Boolean);
  for (const y of then) {
    if (y.finding) lines.push(`When all that is true, it ${SEVERITY[y.finding.severity] ?? y.finding.severity} ${y.finding.path || 'the value'} with reason code ${y.finding.reasonCode || '(none yet)'}.`);
    else if (y.decision) lines.push(`When all that is true, it decides ${y.decision.name} is ${y.decision.value}.`);
    else lines.push('When all that is true, it adds a new fact.');
  }
  const ex: any[] = doc.examples ?? [];
  const t = gate(gates, 'tests');
  const results: any[] = Array.isArray(t?.detail) ? t!.detail.filter((d: any) => d.example) : [];
  const passing = results.filter((r) => r.passed).length;
  lines.push(ex.length ? `It has ${ex.length} example${ex.length === 1 ? '' : 's'}: ${list(ex.map((e) => `"${e.name}"`))}.${results.length ? ` ${passing} of ${results.length} pass.` : ''}` : 'It has no examples yet.');
  const pass = ex.filter((e) => !(e.expect?.findings ?? []).length).length;
  const fail = ex.length - pass;
  const threshold = JSON.stringify(doc.when ?? []).match(/[<>]=?|between|bounds/) || params.length;
  const checklist: Check[] = [
    { ok: !!doc.name && /^[a-z0-9.-]+\.rule\.[a-z0-9-]+\.v\d+$/.test(doc.name), text: 'It has a proper name: scope, rule, short name, version.' },
    { ok: (doc.when ?? []).length > 0, text: 'It says what it looks for (When).' },
    { ok: then.length > 0 && then.every((y: any) => !y.finding || (y.finding.path && y.finding.reasonCode)), text: 'It says what happens, with a path and a reason code (Then).' },
    { ok: pass > 0 && fail > 0, text: 'It has a passing example and a failing example.' },
    ...(threshold ? [{ ok: ex.some((e) => /bound|edge|limit|exactly|december/i.test(e.name ?? '')), text: 'It has a boundary example, exactly at the edge.' }] : []),
    ...gateChecks(gates),
  ];
  const sev = then[0]?.finding;
  return {
    title: doc.title ?? doc.name ?? 'A rule',
    lines,
    picture: [
      { label: (doc.when ?? []).map((c: any) => c.match?.fact).filter(Boolean)[0] ?? 'Facts', sub: 'what it looks at', tone: 'in' },
      { label: 'When', sub: `${(doc.when ?? []).length} condition${(doc.when ?? []).length === 1 ? '' : 's'}`, tone: 'rule' },
      sev ? { label: sev.severity, sub: sev.reasonCode || 'no code', tone: 'bad' } : { label: 'Then', sub: then[0]?.decision ? 'a decision' : 'nothing yet', tone: 'out' },
    ],
    checklist,
    next: nextStep(checklist),
  };
}

function explainRuleset(env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const rules: string[] = doc.rules ?? [];
  const missing = rules.filter((r) => !env.store.doc(r));
  const lines = [
    `This rule set runs ${rules.length} rule${rules.length === 1 ? '' : 's'} together, for ${doc.mode === 'decision' ? 'a decision' : 'checking'}.`,
    `It runs in stages: ${list(doc.stages ?? [])}. A blocking finding in one stage stops the next.`,
    ...rules.map((r) => `${r}${env.store.doc(r)?.title ? `: ${env.store.doc(r).title}` : ''}.`),
  ];
  const checklist = [
    { ok: rules.length > 0, text: 'It has at least one rule.' },
    { ok: missing.length === 0, text: missing.length ? `These rules are not published yet: ${list(missing)}.` : 'Every rule in it is published.' },
    ...gateChecks(gates),
  ];
  return { title: doc.title ?? doc.name, lines, picture: rules.slice(0, 4).map((r) => ({ label: r.split('.').slice(-2, -1)[0] ?? r, sub: 'rule', tone: 'rule' as const })), checklist, next: nextStep(checklist) };
}

function explainTable(_env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const ins = (doc.inputs ?? []).map((i: any) => `${i.name} (${i.expression})`);
  const lines = [
    `This decision table looks at ${list(ins)} and gives ${list(doc.outputs ?? [])}.`,
    `It has ${(doc.rows ?? []).length} rows. ${doc.hitPolicy === 'first' ? 'The first row that matches wins.' : doc.hitPolicy === 'collect' ? 'Every row that matches counts.' : 'At most one row may match.'}`,
    `It has ${(doc.examples ?? []).length} examples.`,
  ];
  const checklist = [{ ok: (doc.rows ?? []).length > 0, text: 'It has rows.' }, { ok: (doc.examples ?? []).length > 0, text: 'It has examples.' }, ...gateChecks(gates)];
  return { title: doc.title ?? doc.name, lines, picture: [{ label: 'Inputs', sub: list((doc.inputs ?? []).map((i: any) => i.name)), tone: 'in' }, { label: 'Rows', sub: String((doc.rows ?? []).length), tone: 'rule' }, { label: 'Outputs', sub: list(doc.outputs ?? []), tone: 'out' }], checklist, next: nextStep(checklist) };
}

// ---------------------------------------------------------------- activities

function explainActivity(env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const outs: any[] = doc.outcomes ?? [];
  const primary = outs.filter((o) => o.primary);
  const lines = [
    `This station ${ACTIVITY_TYPE[doc.type] ?? `does a ${doc.type} job`}, at ${doc.level ?? 'payment'} level. It ${EFFECT[doc.effect] ?? 'has no effect set'}.`,
    `It may read ${list(doc.reads ?? []) || 'nothing yet'}, and may change ${list(doc.writes ?? []) || 'nothing'}. Nothing else.`,
    `Its ways out are ${list(outs.map((o) => `${o.name}${o.primary ? ' (the normal way)' : ''}`)) || 'not set yet'}. A workflow that uses it must say what to do on each of the others.`,
  ];
  if (doc.ruleSets?.length) lines.push(`It runs the rule set${doc.ruleSets.length > 1 ? 's' : ''} ${list(doc.ruleSets)}.`);
  if (doc.ports?.length) lines.push(`It talks through ${list(doc.ports)}.`);
  if (doc.map) lines.push(`It translates ${doc.map.direction === 'out' ? 'from the canonical model into' : 'from'} ${doc.map.sourceModel ?? 'the scheme format'}${doc.map.direction === 'out' ? '' : ' into the canonical model'}, ${(doc.map.fields ?? []).length} fields.`);
  const impl = doc.implementation ?? {};
  lines.push(impl.binding === 'subflow' ? `It is a composite: underneath, the subflow ${impl.subflow} sends, waits and reads.` : impl.binding === 'script' ? `A ${impl.language ?? ''} script does the work: ${impl.script ?? 'not named yet'}.` : impl.binding ? `The ${impl.binding} service ${impl.service ?? ''} does the work.` : 'Nothing is bound to do the work yet.');
  const tests = env.store.byKind('test').filter((t) => t.doc.target === doc.name).map((t) => t.doc);
  const covered = new Set(tests.map((t) => t.expect?.outcome));
  const uncovered = outs.map((o) => o.name).filter((o) => !covered.has(o));
  lines.push(tests.length ? `${tests.length} test${tests.length === 1 ? '' : 's'} target it.` : 'No saved test targets it yet.');
  const needsKey = (doc.effect === 'write' || doc.effect === 'external') && (doc.policy?.retry || doc.type === 'exchange');
  const checklist: Check[] = [
    { ok: !!doc.name && !!doc.type, text: 'It has a name and a type.' },
    { ok: primary.length === 1, text: 'Exactly one outcome is the normal way out.' },
    { ok: (doc.reads ?? []).length > 0, text: 'It says what it reads.' },
    ...(doc.type === 'validate' || doc.type === 'decide' ? [{ ok: (doc.ruleSets ?? []).length > 0, text: 'It names the rule set it runs.' }] : []),
    ...(needsKey ? [{ ok: !!doc.idempotencyKey, text: 'It carries an idempotency key, so a retry is safe.' }] : []),
    { ok: uncovered.length === 0, text: uncovered.length ? `A test for each outcome: none yet for ${list(uncovered)}.` : 'Every outcome has a test.' },
    ...gateChecks(gates),
  ];
  return {
    title: doc.title ?? doc.name ?? 'An activity',
    lines,
    picture: [{ label: 'Reads', sub: list(doc.reads ?? []) || 'nothing', tone: 'in' }, { label: doc.title ?? 'Station', sub: doc.type, tone: 'step' }, ...outs.slice(0, 4).map((o) => ({ label: o.name, sub: o.primary ? 'normal' : 'handle', tone: (o.primary ? 'ok' : 'bad') as PictureNode['tone'] }))],
    checklist,
    next: nextStep(checklist),
  };
}

function explainSimulator(_env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const sc: any[] = doc.scenarios ?? [];
  const lines = [`This simulator plays ${doc.standsFor ?? 'the party'} behind ${doc.port ?? 'a port'}, in tests only. It is never shipped to a bank.`, `It knows ${sc.length} scenario${sc.length === 1 ? '' : 's'}: ${list(sc.map((s) => s.name))}.`];
  const checklist = [{ ok: !!doc.port, text: 'It names the port it plays.' }, ...gateChecks(gates)];
  return { title: doc.title ?? doc.name, lines, picture: [{ label: 'Our workflow', tone: 'in' }, { label: doc.port?.split('.').slice(-2, -1)[0] ?? 'port', sub: 'port', tone: 'step' }, { label: 'Simulator', sub: `${sc.length} scenarios`, tone: 'out' }], checklist, next: nextStep(checklist) };
}

function explainPort(_env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const ops: any[] = doc.operations ?? [];
  const lines = [`This port is the door to ${doc.party ?? 'another party'}.`, ...ops.map((o) => `${o.name}: ${o.style === 'send' ? 'we send' : o.style === 'receive' ? 'we receive' : o.style}.`), 'Every port needs a simulator, and the two are published together.'];
  const checklist = [{ ok: ops.length > 0, text: 'It has at least one operation.' }, ...gateChecks(gates)];
  return { title: doc.title ?? doc.name, lines, checklist, next: nextStep(checklist) };
}

function explainScheme(_env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const msgs: any[] = doc.messages ?? [];
  const lines = [`This is the scheme ${doc.scope}. Its dispatch table is the routing map: it says what happens to each message.`];
  for (const m of msgs) {
    const d = m.disposition ?? {};
    if (d.start) lines.push(`${m.id} (${m.title ?? ''}) starts a ${d.start.direction ?? ''} ${d.start.segment ?? ''} ${d.start.kind ?? ''}, read by ${d.start.intakeMap ?? 'no intake map'}, with the leg ${d.start.leg ?? '(none)'} in the slot.`.replace(/ {2,}/g, ' '));
    else if (d.deliver) lines.push(`${m.id} is delivered to the workflow that waits for it, matched by ${d.deliver.correlateBy}.`);
    else if (d.sentBy) lines.push(`${m.id} is sent by ${d.sentBy}.`);
    else lines.push(`${m.id}: ${JSON.stringify(d)}.`);
  }
  const checklist = [{ ok: msgs.some((m) => m.disposition?.start), text: 'At least one message starts a payment.' }, ...gateChecks(gates)];
  return { title: doc.title ?? doc.scope, lines, checklist, next: nextStep(checklist) };
}

// ---------------------------------------------------------------- workflows and hooks

function action(a: any): string {
  if (Array.isArray(a)) return `run ${a.length} step${a.length === 1 ? '' : 's'}`;
  if (a && 'end' in a) return `end as ${a.end}${a.compensate ? ', undoing what was done' : ''}`;
  if (a && 'return' in a) return `give back ${a.return}`;
  if (a && 'continue' in a) return 'note it and carry on';
  return 'do what the YAML says';
}

function steps(env: StudioEnv, list_: any[], out: string[], prefix = '', depth = 0) {
  let n = 0;
  for (const st of list_ ?? []) {
    n++;
    const no = `${prefix}${n}`;
    const pad = '  '.repeat(depth);
    if (st?.call) {
      const title = env.store.doc(st.call)?.title;
      const handled = Object.entries<any>(st.handle ?? {}).map(([o, a]) => `if ${o}, ${action(a)}`);
      out.push(`${pad}${no}. ${st.id ?? 'A step'}: uses ${title ? `"${title}" (${st.call})` : st.call || 'no block yet'}.${handled.length ? ` ${handled.join('; ')}.` : ''}`);
    } else if (st?.forEach) {
      out.push(`${pad}${no}. For each ${st.forEach.in === 'items[]' ? 'line of the file' : st.forEach.in}${st.forEach.onItemFailure === 'stop' ? ', stopping at the first that fails' : ''}:`);
      steps(env, st.forEach.do, out, `${no}.`, depth + 1);
    } else if (st?.switch) {
      out.push(`${pad}${no}. Choose a branch by ${JSON.stringify(st.switch.by)}: ${list(Object.keys(st.switch.cases ?? {}))}${'default' in st.switch ? ', or the default' : ''}.`);
    } else if (st?.parallel) {
      out.push(`${pad}${no}. Run ${(st.parallel.branches ?? []).length} branches at the same time, then join (${st.parallel.join ?? 'all'}).`);
    } else if (st?.slot) {
      out.push(`${pad}${no}. ${st.id ?? 'The slot'}: the scheme's leg runs here, in the slot ${st.slot}.`);
    } else if (st?.hook) {
      out.push(`${pad}${no}. A hook point, ${st.hook}: a bank may plug its own step in here.`);
    } else if (st && 'end' in st) {
      out.push(`${pad}${no}. End as ${st.end}.`);
    } else if (st && 'return' in st) {
      out.push(`${pad}${no}. Give back ${st.return}.`);
    } else if (st?.await) {
      out.push(`${pad}${no}. Wait for ${st.await.message || 'a message'}, matched by ${st.await.correlateBy}.`);
    } else if (st?.wait) {
      out.push(`${pad}${no}. Wait ${st.wait}.`);
    } else {
      out.push(`${pad}${no}. ${Object.keys(st ?? {})[0] ?? 'A step'}.`);
    }
  }
}

function explainWorkflow(env: StudioEnv, doc: any, gates?: GateLike[], selected?: string): Explanation {
  const lines: string[] = [];
  if (doc.kind === 'hook') lines.push(`This is a bank's hook. It plugs into the hook point ${doc.implements?.hookPoint} of ${doc.implements?.of}. It is a ${doc.type} hook: ${doc.type === 'halt' ? 'it may hold or reject the payment' : doc.type === 'transform' ? 'it adds the bank\'s own values' : 'it only tells someone'}.`);
  else lines.push(`This is ${WF_KIND[doc.wfKind] ?? `a ${doc.wfKind} workflow`}.`);
  if (doc.implements?.slot) lines.push(`It fills the ${doc.implements.slot} slot of ${list(doc.implements.of ?? [])}.`);
  if (doc.entry?.reads) lines.push(`It may read ${list(doc.entry.reads)}.`);
  const outcomes = doc.exit?.outcomes ?? doc.outcomes;
  if (outcomes) lines.push(`It can end as ${list(outcomes)}.`);
  lines.push('Step by step:');
  steps(env, doc.do ?? [], lines);
  if (doc.finally?.length) lines.push(`Whatever happens, it finally runs ${doc.finally.length} step${doc.finally.length === 1 ? '' : 's'}.`);
  if (selected) {
    const find = (xs: any[]): any => { for (const s of xs ?? []) { if (s?.id === selected) return s; const inner = find(s?.forEach?.do ?? s?.loop?.do ?? s?.try ?? []); if (inner) return inner; } };
    const s = find(doc.do ?? []);
    if (s?.call) {
      const act = env.store.doc(s.call);
      const outs = (act?.outcomes ?? []).map((o: any) => o.name);
      lines.push(`The selected step, ${selected}, uses ${act?.title ?? s.call}. Its ways out are ${list(outs)}. ${outs.filter((o: string) => !(act?.outcomes ?? []).find((x: any) => x.name === o)?.primary && !(s.handle ?? {})[o]).length ? 'Some are not handled yet.' : 'Every one is handled.'}`);
    }
  }
  const checklist = [
    { ok: (doc.do ?? []).length > 0, text: 'It has steps.' },
    { ok: JSON.stringify(doc.do ?? []).includes('"end"') || doc.kind === 'hook' || doc.wfKind === 'subflow', text: 'Every path ends with an outcome.' },
    ...gateChecks(gates),
  ];
  const top = (doc.do ?? []).slice(0, 6).map((s: any) => ({ label: s.id ?? (s.forEach ? 'for each' : 'end' in (s ?? {}) ? `end ${s.end}` : Object.keys(s ?? {})[0]), sub: s.call ? s.call.split('.').slice(-2, -1)[0] : undefined, tone: ('end' in (s ?? {}) ? 'out' : 'step') as PictureNode['tone'] }));
  return { title: doc.title ?? doc.name ?? 'A workflow', lines, picture: top, checklist, next: nextStep(checklist) };
}

// ---------------------------------------------------------------- configuration, tests, bench

function explainConfiguration(_env: StudioEnv, doc: any, issues?: any[]): Explanation {
  const lines = [`This is ${doc.tenant ?? 'the bank'}'s configuration, release ${doc.release}.`];
  if (doc.accepts?.length) lines.push(`It accepts the shared releases ${list(doc.accepts.map((a: any) => `${a.scope} ${a.release}`))}.`);
  for (const v of doc.values ?? []) lines.push(`From ${v.validFrom}, ${v.parameter} is ${JSON.stringify(v.value)}.`);
  for (const b of doc.bindings ?? []) lines.push(`From ${b.validFrom}, the hook ${b.hook} runs at ${b.hookPoint}.`);
  const errs = (issues ?? []).filter((i) => i.severity === 'error');
  const checklist = [{ ok: errs.length === 0, text: errs.length ? `It is refused: ${errs.slice(0, 2).map((e) => e.message).join('; ')}` : 'It passes every check: values within bounds, no overlapping dates.' }];
  return { title: `Configuration ${doc.release ?? ''}`, lines, checklist, next: errs.length ? 'Next: fix the value or binding the check names.' : 'Save it as a new release. The bank then runs the compatibility run before accepting new shared releases.' };
}

function explainTest(_env: StudioEnv, doc: any, gates?: GateLike[]): Explanation {
  const lines = [`This test runs ${doc.target} at ${doc.level ?? 'its own'} level, on ${doc.evaluationDate}.`];
  for (const b of doc.bench ?? []) lines.push(`${b.port} is played by ${b.simulator}, scenario ${b.scenario}.`);
  const e = doc.expect ?? {};
  lines.push(`It expects ${[e.outcome && `the outcome ${e.outcome}`, e.reasonCode && `reason code ${e.reasonCode}`, e.trace && `the trace ${e.trace.join(', ')}`].filter(Boolean).join(', ') || 'nothing yet'}.`);
  const checklist = [{ ok: !!e.outcome, text: 'It expects an outcome.' }, ...gateChecks(gates)];
  return { title: doc.title ?? doc.id, lines, checklist, next: nextStep(checklist) };
}

export function explainBench(_env: StudioEnv, setup: any, report?: any): Explanation {
  const lines: string[] = [];
  if (!setup?.target) lines.push('Nothing is chosen to run yet. Choose a target: a published workflow, hook or activity.');
  else lines.push(`The bench will run ${setup.target}${setup.tenant ? ` for ${setup.tenant}` : ''}.`);
  for (const b of setup?.bench ?? []) if (b.port) lines.push(`${b.port} is played by ${b.simulator || '(no simulator yet)'}, scenario ${b.scenario || '(none yet)'}.`);
  if (setup?.inbound) lines.push(`It starts from the sample ${setup.inbound}${setup.message ? `, as the message ${setup.message}` : ''}.`);
  else if (setup?.context) lines.push('It starts from the context you gave as JSON.');
  const checklist: Check[] = [{ ok: !!setup?.target, text: 'A target is chosen.' }, { ok: (setup?.bench ?? []).every((b: any) => b.port && b.simulator && b.scenario), text: 'Every bound port has a simulator and a scenario.' }];
  if (report) {
    lines.push(`The last run ended as ${report.outcome}${report.reasonCode ? `, reason ${report.reasonCode}` : ''}.`);
    for (const t of report.trace ?? []) {
      const [step, out] = String(t).split(':');
      lines.push(`  ${step}: ${out}.`);
    }
    if (report.error) lines.push(`It stopped with an error: ${String(report.error).split('\n')[0]}`);
  }
  return {
    title: 'Test bench',
    lines,
    picture: report?.trace?.slice(0, 7).map((t: string) => { const [s, o] = t.split(':'); return { label: s, sub: o, tone: /reject|fail|declin|error/.test(o ?? '') ? 'bad' : 'ok' }; }),
    checklist,
    next: !setup?.target ? 'Next: choose a target.' : report ? 'If the result is what you want, save the run as a test.' : 'Next: run the bench.',
  };
}

/** Explain any document the studio edits. */
export function explainDoc(env: StudioEnv, doc: any, opts: { gates?: GateLike[]; issues?: any[]; selected?: string } = {}): Explanation {
  if (!doc || typeof doc !== 'object') return { title: 'Nothing yet', lines: ['Start a document, or open one, and it is explained here as you work.'], checklist: [] };
  const g = opts.gates;
  switch (doc.kind) {
    case 'rule': return explainRule(env, doc, g);
    case 'ruleset': return explainRuleset(env, doc, g);
    case 'table': return explainTable(env, doc, g);
    case 'activity': return explainActivity(env, doc, g);
    case 'simulator': return explainSimulator(env, doc, g);
    case 'port': return explainPort(env, doc, g);
    case 'scheme': return explainScheme(env, doc, g);
    case 'workflow': case 'hook': return explainWorkflow(env, doc, g, opts.selected);
    case 'configuration-set': return explainConfiguration(env, doc, opts.issues);
    case 'test': return explainTest(env, doc, g);
    default: return { title: doc.name ?? doc.kind ?? 'A document', lines: [`A ${doc.kind ?? 'document'}.`], checklist: gateChecks(g), next: nextStep(gateChecks(g)) };
  }
}
