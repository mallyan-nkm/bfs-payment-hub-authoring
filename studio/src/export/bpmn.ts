// BPMN 2.0 generation for Flowable (C22). Every activity is an external worker task, a wait
// state, so timer boundaries apply (R-09-12). Await is an event-based gateway between a message
// catch and a timer. A slot is a call activity. A deadline is an interrupting timer boundary on
// the main sub-process. Every end goes through the finally process before its end event.
// The output is reproducible: the same input gives the same bytes (R-09-15).

import { effectivePolicy } from '../interp/policy.js';

interface Ctx {
  queueOf: (name: string) => string;
  resolve: (name: string) => any;
}

interface Frag { entry: string; exits: string[] }

class Builder {
  els: string[] = [];
  flows: string[] = [];
  n = 0;
  ends = new Map<string, string>(); // outcome -> node that leads to finally and the end
  constructor(readonly wf: any, readonly ctx: Ctx, readonly inSub: boolean) {}

  id(prefix: string) {
    this.n += 1;
    return `${prefix}_${this.n}`;
  }

  flow(from: string, to: string, cond?: string, name?: string) {
    const id = this.id('flow');
    this.flows.push(cond
      ? `    <sequenceFlow id="${id}" sourceRef="${from}" targetRef="${to}"${name ? ` name="${esc(name)}"` : ''}><conditionExpression xsi:type="tFormalExpression"><![CDATA[${cond}]]></conditionExpression></sequenceFlow>`
      : `    <sequenceFlow id="${id}" sourceRef="${from}" targetRef="${to}"${name ? ` name="${esc(name)}"` : ''}/>`);
  }

  endFor(outcome: string): string {
    let id = this.ends.get(outcome);
    if (!id) {
      id = `end_${safe(outcome)}`;
      this.ends.set(outcome, id);
    }
    return id;
  }

  steps(steps: any[]): Frag | null {
    let first: string | undefined;
    let exits: string[] = [];
    for (const st of steps ?? []) {
      const f = this.step(st);
      if (!f) continue;
      if (!first) first = f.entry;
      for (const e of exits) this.flow(e, f.entry);
      exits = f.exits;
    }
    return first ? { entry: first, exits } : null;
  }

  action(action: any, from: string, cond: string, label: string) {
    if (Array.isArray(action)) {
      const f = this.steps(action);
      if (f) {
        this.flow(from, f.entry, cond, label);
        return f.exits;
      }
      return [];
    }
    if (action?.end) {
      this.flow(from, this.endFor(action.end), cond, label);
      return [];
    }
    return null; // continue
  }

  step(st: any): Frag | null {
    if ('call' in st) return this.call(st);
    if ('end' in st && !('call' in st)) {
      const id = this.id('goto');
      this.els.push(`    <intermediateThrowEvent id="${id}" name="end:${esc(st.end)}"/>`);
      this.flow(id, this.endFor(st.end));
      return { entry: id, exits: [] };
    }
    if ('switch' in st) {
      const gw = this.id('switch');
      const join = this.id('join');
      const by = st.switch.by.outcomeOf ? `outcome_${safe(st.switch.by.outcomeOf)}` : `decision_${safe(st.switch.by.decision)}`;
      this.els.push(`    <exclusiveGateway id="${gw}" name="switch ${esc(by)}" default="${gw}_default"/>`, `    <exclusiveGateway id="${join}"/>`);
      for (const [k, v] of Object.entries<any>(st.switch.cases ?? {})) {
        const f = this.steps(v);
        if (f) {
          this.flow(gw, f.entry, `\${${by} == '${k}'}`, k);
          f.exits.forEach((e) => this.flow(e, join));
        }
      }
      const d = this.steps(st.switch.default ?? []);
      const did = `${gw}_default`;
      if (d) {
        this.flows.push(`    <sequenceFlow id="${did}" sourceRef="${gw}" targetRef="${d.entry}"/>`);
        d.exits.forEach((e) => this.flow(e, join));
      } else this.flows.push(`    <sequenceFlow id="${did}" sourceRef="${gw}" targetRef="${join}"/>`);
      return { entry: gw, exits: [join] };
    }
    if ('parallel' in st) {
      const fork = this.id('fork');
      const join = this.id('joinAll');
      this.els.push(`    <parallelGateway id="${fork}"/>`, `    <parallelGateway id="${join}" name="join:all"/>`);
      for (const b of st.parallel.branches ?? []) {
        const f = this.steps(b);
        if (!f) continue;
        this.flow(fork, f.entry);
        f.exits.forEach((e) => this.flow(e, join));
      }
      return { entry: fork, exits: [join] };
    }
    if ('forEach' in st) {
      const id = this.id('forEach');
      const inner = new Builder(this.wf, this.ctx, true);
      inner.n = this.n * 100;
      const f = inner.steps(st.forEach.do);
      this.n = inner.n;
      const seq = (st.forEach.concurrency ?? 1) <= 1;
      this.els.push(`    <subProcess id="${id}" name="forEach ${esc(st.forEach.in)}">`,
        `      <multiInstanceLoopCharacteristics isSequential="${seq}" flowable:collection="items" flowable:elementVariable="item"/>`,
        ...inner.body(f, 'item'), `    </subProcess>`);
      return { entry: id, exits: [id] };
    }
    if ('loop' in st) {
      const start = this.id('loopStart');
      const check = this.id('loopUntil');
      const timer = this.id('loopEvery');
      this.els.push(`    <exclusiveGateway id="${start}"/>`, `    <exclusiveGateway id="${check}" name="until" default="${check}_again"/>`);
      const f = this.steps(st.loop.do);
      if (f) {
        this.flow(start, f.entry);
        f.exits.forEach((e) => this.flow(e, check));
      } else this.flow(start, check);
      const done = this.id('loopDone');
      this.els.push(`    <exclusiveGateway id="${done}"/>`);
      const until = st.loop.until?.outcomeOf ? `(${(st.loop.until.in ?? []).map((o: string) => `outcome_${safe(st.loop.until.outcomeOf)} == '${o}'`).join(' || ') || 'false'})` : 'false';
      this.flow(check, done, `\${${until} || loopCount_${start} >= ${st.loop.max ?? 1}}`);
      if (st.loop.every) {
        this.els.push(`    <intermediateCatchEvent id="${timer}" name="every ${st.loop.every}"><timerEventDefinition><timeDuration>${st.loop.every}</timeDuration></timerEventDefinition></intermediateCatchEvent>`);
        this.flows.push(`    <sequenceFlow id="${check}_again" sourceRef="${check}" targetRef="${timer}"/>`);
        this.flow(timer, start);
      } else this.flows.push(`    <sequenceFlow id="${check}_again" sourceRef="${check}" targetRef="${start}"/>`);
      return { entry: start, exits: [done] };
    }
    if ('await' in st) {
      const aw = st.await;
      const gw = this.id('await');
      const msg = this.id('message');
      const tmr = this.id('timeout');
      const id = st.id ?? 'await';
      this.els.push(`    <eventBasedGateway id="${gw}" name="await ${esc(id)}"/>`,
        `    <intermediateCatchEvent id="${msg}" name="${esc(id)}:received"><messageEventDefinition messageRef="msg_${safe(aw.message)}"/></intermediateCatchEvent>`);
      this.flow(gw, msg);
      const exits = [msg];
      if (aw.timeout) {
        this.els.push(`    <intermediateCatchEvent id="${tmr}" name="${esc(id)}:timeout"><timerEventDefinition><timeDuration>${aw.timeout}</timeDuration></timerEventDefinition>${aw.calendar === 'business' ? '<documentation>Business calendar: the binding converts the duration to business days.</documentation>' : ''}</intermediateCatchEvent>`);
        this.flow(gw, tmr);
        const after = aw.onTimeout ? this.action(aw.onTimeout, tmr, '', 'timeout') : null;
        if (after === null) exits.push(tmr);
        else exits.push(...after);
      }
      return { entry: gw, exits };
    }
    if ('wait' in st) {
      const id = this.id('wait');
      this.els.push(`    <intermediateCatchEvent id="${id}" name="wait:${st.wait}"><timerEventDefinition><timeDuration>${st.wait}</timeDuration></timerEventDefinition></intermediateCatchEvent>`);
      return { entry: id, exits: [id] };
    }
    if ('slot' in st) {
      const id = this.id('slot');
      this.els.push(`    <callActivity id="${id}" name="${esc(st.id ?? st.slot)}" calledElement="\${leg_${safe(st.slot)}}" flowable:inheritBusinessKey="true"><extensionElements><flowable:out source="outcome" target="outcome_${safe(st.id ?? st.slot)}"/></extensionElements></callActivity>`);
      return this.outcomes(id, st, (this.wf.slots ?? []).find((s: any) => s.name === st.slot)?.contract?.primaryOutcome);
    }
    if ('use' in st) {
      const seg = this.ctx.resolve(st.use);
      return this.steps(seg?.do ?? []);
    }
    if ('try' in st) return this.steps(st.try);
    if ('hook' in st) {
      const id = this.id('hook');
      this.els.push(`    <callActivity id="${id}" name="hook ${esc(st.hook)}" calledElement="\${hook_${safe(st.hook)}}" flowable:inheritBusinessKey="true"><documentation>The tenant's binding names the hook; an unbound hook point is skipped by the binding.</documentation></callActivity>`);
      return { entry: id, exits: [id] };
    }
    return null;
  }

  call(st: any): Frag {
    const act = this.ctx.resolve(st.call);
    const id = this.id('task');
    const label = st.id ?? st.call;
    const release = act?.release ?? '0.0.0';
    const topic = `${this.ctx.queueOf(st.call)}:${st.call}@${release}`;
    const pol = act ? effectivePolicy(act, st.policy) : undefined;
    const retry = pol && pol.maxAttempts.value > 1 ? `R${pol.maxAttempts.value - 1}/${pol.firstInterval.value}` : 'R0/PT0S';
    this.els.push(`    <serviceTask id="${id}" name="${esc(label)}" flowable:type="external-worker" flowable:topic="${esc(topic)}"><extensionElements><flowable:failedJobRetryTimeCycle>${retry}</flowable:failedJobRetryTimeCycle></extensionElements></serviceTask>`);
    if (pol?.attemptTimeout.value) {
      const b = this.id('attemptTimeout');
      this.els.push(`    <boundaryEvent id="${b}" attachedToRef="${id}" cancelActivity="true"><timerEventDefinition><timeDuration>${pol.attemptTimeout.value}</timeDuration></timerEventDefinition></boundaryEvent>`);
      // The timed-out attempt goes round the retry loop: back to the task while attempts remain.
      this.flow(b, id, `\${attempts_${id} < ${pol.maxAttempts.value}}`, 'retry');
    }
    const primary = act?.outcomes?.find((o: any) => o.primary)?.name;
    return this.outcomes(id, st, primary);
  }

  outcomes(id: string, st: any, primary?: string): Frag {
    const handle = st.handle ?? {};
    if (!Object.keys(handle).length) return { entry: id, exits: [id] };
    const gw = this.id('outcome');
    const v = `outcome_${safe(st.id ?? st.call ?? st.slot)}`;
    this.els.push(`    <exclusiveGateway id="${gw}" name="${esc(v)}" default="${gw}_primary"/>`);
    this.flow(id, gw);
    const exits: string[] = [];
    const join = this.id('continue');
    this.els.push(`    <exclusiveGateway id="${join}"/>`);
    for (const [o, a] of Object.entries<any>(handle)) {
      const after = this.action(a, gw, `\${${v} == '${o}'}`, o);
      if (after === null) this.flow(gw, join, `\${${v} == '${o}'}`, `${o}, continue`);
      else after.forEach((e) => this.flow(e, join));
    }
    this.flows.push(`    <sequenceFlow id="${gw}_primary" sourceRef="${gw}" targetRef="${join}" name="${esc(primary ?? 'primary')}"/>`);
    exits.push(join);
    return { entry: gw, exits };
  }

  body(f: Frag | null, prefix: string): string[] {
    const s = `${prefix}_start`;
    const e = `${prefix}_done`;
    const out = [`      <startEvent id="${s}"/>`, `      <endEvent id="${e}"/>`];
    if (f) {
      this.flows.unshift(`    <sequenceFlow id="${prefix}_in" sourceRef="${s}" targetRef="${f.entry}"/>`);
      f.exits.forEach((x, i) => this.flows.push(`    <sequenceFlow id="${prefix}_out_${i}" sourceRef="${x}" targetRef="${e}"/>`));
    } else this.flows.push(`    <sequenceFlow id="${prefix}_in" sourceRef="${s}" targetRef="${e}"/>`);
    for (const [outcome, id] of this.ends) out.push(`      <endEvent id="${id}" name="end:${esc(outcome)}"><errorEventDefinition errorRef="itemEnd_${safe(outcome)}"/></endEvent>`);
    return [...out, ...this.els.map((x) => '  ' + x), ...this.flows.map((x) => '  ' + x)];
  }
}

function esc(s: any) {
  return String(s).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function safe(s: string) {
  return String(s).replace(/[^A-Za-z0-9_]/g, '_');
}

export function generateBpmn(wf: any, ctx: Ctx): string {
  const pid = safe(`${wf.name}@${wf.release}`);
  const b = new Builder(wf, ctx, false);
  const main = b.steps(wf.do ?? []);
  const primaryExit = wf.exit?.outcomes?.[0] ?? 'done';
  const lines: string[] = [];
  const messages = new Set<string>();
  const collect = (steps: any[]) => {
    for (const st of steps ?? []) {
      if (st.await) messages.add(st.await.message);
      for (const a of Object.values(st.handle ?? {})) if (Array.isArray(a)) collect(a);
      for (const k of ['try', 'catch']) if (Array.isArray(st[k])) collect(st[k]);
      if (st.loop) collect(st.loop.do);
      if (st.forEach) collect(st.forEach.do);
      if (st.parallel) for (const x of st.parallel.branches ?? []) collect(x);
      if (st.switch) {
        for (const c of Object.values<any>(st.switch.cases ?? {})) collect(c);
        collect(st.switch.default);
      }
    }
  };
  collect(wf.do);
  lines.push('<?xml version="1.0" encoding="UTF-8"?>');
  lines.push(`<!-- Generated by the TransactHub Designer Studio from ${wf.name}@${wf.release} (DSL ${wf.dsl ?? '1.0.0'}). Do not edit; regenerate. -->`);
  lines.push('<definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:flowable="http://flowable.org/bpmn" targetNamespace="urn:transacthub:workflows">');
  for (const m of [...messages].sort()) lines.push(`  <message id="msg_${safe(m)}" name="${esc(m)}"/>`);
  lines.push(`  <process id="${pid}" name="${esc(wf.title ?? wf.name)}" isExecutable="true">`);
  lines.push('    <startEvent id="start"/>');
  if (!b.ends.has(primaryExit)) b.endFor(primaryExit);
  const finallyId = 'finally';
  const hasFinally = (wf.finally ?? []).length > 0;
  if (wf.deadline?.after) {
    // The main flow is a sub-process with an interrupting timer: the deadline is a timer, never an engine execution timeout (R-05-20).
    const inner = new Builder(wf, ctx, true);
    const f = inner.steps(wf.do ?? []);
    if (!inner.ends.has(primaryExit)) inner.endFor(primaryExit);
    lines.push(`    <subProcess id="main" name="main">`, ...inner.body(f, 'main'), '    </subProcess>');
    lines.push(`    <boundaryEvent id="deadline" attachedToRef="main" cancelActivity="true"><timerEventDefinition><timeDuration>${wf.deadline.after}</timeDuration></timerEventDefinition></boundaryEvent>`);
    for (const [o] of inner.ends) lines.push(`    <boundaryEvent id="caught_${safe(o)}" attachedToRef="main"><errorEventDefinition errorRef="itemEnd_${safe(o)}"/></boundaryEvent>`);
    lines.push(`    <sequenceFlow id="f_start" sourceRef="start" targetRef="main"/>`);
    const d = b.steps(wf.deadline.do ?? []);
    if (d) lines.push(`    <sequenceFlow id="f_deadline" sourceRef="deadline" targetRef="${d.entry}"/>`);
    for (const [o] of inner.ends) lines.push(`    <sequenceFlow id="f_caught_${safe(o)}" sourceRef="caught_${safe(o)}" targetRef="${b.endFor(o)}"/>`);
  } else if (main) {
    lines.push(`    <sequenceFlow id="f_start" sourceRef="start" targetRef="${main.entry}"/>`);
    main.exits.forEach((e, i) => b.flow(e, b.endFor(primaryExit), undefined, `fall-through ${i}`));
  }
  // Every end goes through finally (R-05-14), then ends with its outcome.
  for (const [o, id] of [...b.ends].sort(([a], [c]) => a.localeCompare(c))) {
    lines.push(`    <intermediateThrowEvent id="${id}" name="end:${esc(o)}"/>`);
    if (hasFinally) {
      lines.push(`    <callActivity id="${id}_finally" name="finally" calledElement="${pid}__${finallyId}" flowable:inheritBusinessKey="true"/>`);
      lines.push(`    <sequenceFlow id="${id}_to_finally" sourceRef="${id}" targetRef="${id}_finally"/>`);
      lines.push(`    <sequenceFlow id="${id}_to_exit" sourceRef="${id}_finally" targetRef="${id}_exit"/>`);
    } else lines.push(`    <sequenceFlow id="${id}_to_exit" sourceRef="${id}" targetRef="${id}_exit"/>`);
    lines.push(`    <endEvent id="${id}_exit" name="${esc(o)}"><extensionElements><flowable:executionListener event="start" expression="\${execution.setVariable('outcome', '${esc(o)}')}"/></extensionElements></endEvent>`);
  }
  lines.push(...b.els, ...b.flows);
  lines.push('  </process>');
  if (hasFinally) {
    const fb = new Builder(wf, ctx, false);
    const f = fb.steps(wf.finally);
    lines.push(`  <process id="${pid}__${finallyId}" name="finally" isExecutable="true">`, '    <startEvent id="fstart"/>', '    <endEvent id="fend"/>');
    if (f) {
      lines.push(`    <sequenceFlow id="ff_start" sourceRef="fstart" targetRef="${f.entry}"/>`);
      f.exits.forEach((e) => fb.flow(e, 'fend'));
    }
    lines.push(...fb.els, ...fb.flows, '  </process>');
  }
  lines.push('</definitions>', '');
  return lines.join('\n');
}

// ----------------------------------------------------------- portability (C22-S5)

export interface PortabilityItem { flow: string; construct: string; target: string; note: string; severity: 'info' | 'warning' }

export function portabilityReport(flows: any[], target: string): PortabilityItem[] {
  const out: PortabilityItem[] = [];
  const seen = new Set<string>();
  for (const f of flows) {
    const add = (construct: string, note: string, severity: 'info' | 'warning' = 'info') => {
      const k = `${f.name}|${construct}|${note}`;
      if (!seen.has(k)) { seen.add(k); out.push({ flow: f.name, construct, target, note, severity }); }
    };
    const steps: any[] = [];
    const walk = (xs: any[]) => {
      for (const st of xs ?? []) {
        steps.push(st);
        for (const a of Object.values(st.handle ?? {})) if (Array.isArray(a)) walk(a);
        for (const k of ['try', 'catch']) if (Array.isArray(st[k])) walk(st[k]);
        if (st.loop) walk(st.loop.do);
        if (st.forEach) walk(st.forEach.do);
        if (st.parallel) for (const x of st.parallel.branches ?? []) walk(x);
        if (st.switch) { for (const c of Object.values<any>(st.switch.cases ?? {})) walk(c); walk(st.switch.default); }
      }
    };
    walk(f.do);
    walk(f.finally);
    if (f.synchronous) add('synchronous', 'Marked synchronous (D9 open): the binding decides whether steps run as durable tasks.', 'warning');
    for (const st of steps) {
      if (st.await) {
        if (target === 'flowable') add('await', 'A message that arrives before the catch event is held by the binding until the catch is waiting (R-09-16).', 'warning');
        if (target === 'temporal') add('await', 'A signal with a timer; early signals are kept in workflow state.');
        if (target === 'dtfx') add('await', 'An external event raced with a durable timer; early events are kept in orchestration state.');
        if (st.await.calendar === 'business') add('await', 'Business calendar: the binding converts the timeout to business days.', 'warning');
      }
      if (st.forEach && (target === 'temporal' || target === 'dtfx')) add('forEach', 'Large batches are chunked with continue-as-new; the chunk size comes from the binding (R-09-13).');
      if (st.call && st.policy?.attemptTimeout && target === 'dtfx') add('policy', `Step ${st.id ?? st.call}: an attempt timeout abandons the late result; it does not cancel the task (R-09-11).`, 'warning');
      if (st.call && target === 'flowable' && st.policy?.retry?.backoff && st.policy.retry.backoff !== 1) add('policy', `Step ${st.id ?? st.call}: backoff is modelled as a retry loop with timers.`, 'warning');
      if (st.hook) add('hook', 'Bound per tenant at instance start; an unbound hook point is skipped.');
    }
    if (f.deadline && target === 'flowable') add('deadline', 'An interrupting timer boundary on the main sub-process.');
    if (f.wfKind === 'handler') add('handler', 'A plain request handler at the interop boundary; no durable instance (R-09-14).');
  }
  return out;
}
