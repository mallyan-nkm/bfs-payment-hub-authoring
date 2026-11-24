import { useEffect, useRef, useState } from 'react';
import { get, post, refusal } from '../api';
import { useAssistReg, type AssistReg } from '../assist';
import { Markdown } from '../markdown';
import { Alert, Badge, useDebounced, useStudio } from '../ui';
import { Button, Link, Tab, TabList, Textarea } from '@fluentui/react-components';
import { DismissRegular, SparkleRegular } from '@fluentui/react-icons';

// The assistant beside every surface (I-29). Two tabs:
// - What is happening: help on the field in focus, the document in plain words with a small
//   picture, a checklist of what is left before it can be published, and the next step. No model.
// - Ask: the inline agent. It sees the surface, the document and its checks; it asks before every
//   change (allow once, allow always, deny), and it, or the designer, can undo what it changed.

type Change = { id: string; tool: string; summary: string; before: any; after: any };
type Pending = { toolUseId: string; tool: string; input: any; summary: string; preview: { changed?: string[]; check?: string; detail?: string }; results: any[]; index: number; model?: string; allowAlways?: boolean };
type Flag = { toolUseId: string; tool: string; excerpt: string };
type Conv = { messages: any[]; history: Change[]; pending?: Pending; undone: string[]; flagged?: Flag[] };

const ACTIVITY: Record<string, (i: any) => string> = {
  get_document: () => 'Read the document',
  check_document: () => 'Ran the checks',
  explain_document: () => 'Read the explanation',
  field_help: (i) => `Read the help for ${i.field}`,
  search_catalogue: (i) => `Searched the catalogue for "${i.query}"`,
  read_artefact: (i) => `Read ${i.name}`,
  list_samples: () => 'Listed the sample files',
  run_bench: (i) => `Ran ${i.target} on the bench`,
  edit_document: (i) => `Changed the document: ${i.summary ?? ''}`,
  undo_change: (i) => `Undid ${i.changeId}`,
  save_draft: () => 'Saved the draft',
  save_test: () => 'Saved a test',
  save_sample: (i) => `Saved the sample ${i.name}`,
};

function suggestions(reg?: AssistReg): string[] {
  if (!reg) return ['What can I do on this page?'];
  const k = reg.doc?.kind ?? reg.kind;
  switch (reg.surface) {
    case 'designer': return ['Explain this workflow', 'Explain the selected step', 'Handle every outcome', 'Add a step that …', 'Fix what is red', 'Build tests for this workflow, one per outcome'];
    case 'tenant': return ['Explain this configuration', 'Set … to a value within its bounds', 'Bind a hook at …', 'What will the compatibility run check?'];
    case 'bench': return ['Explain the last run', 'Why did it end like this?', 'Set up a run that ends as …', 'Build a test for each outcome of the target'];
  }
  const common = ['Explain this', 'Fix what is red'];
  switch (k) {
    case 'rule': return ['Explain this rule', 'Build a rule that checks …', 'Write the examples, with a boundary case', 'Make the threshold a bank parameter', ...common.slice(1)];
    case 'activity': return ['Explain this activity', 'Which outcomes should it have?', 'Build a test for each outcome', 'Is a retry safe here?', ...common.slice(1)];
    case 'table': return [...common, 'Add an example for each row'];
    case 'simulator': return [...common, 'Add every missing standard scenario'];
    default: return common;
  }
}

function loadAllow(surface: string): string[] {
  try {
    return JSON.parse(sessionStorage.getItem(`th.allow.${surface}`) ?? '[]');
  } catch {
    return [];
  }
}
function saveAllow(surface: string, a: string[]) {
  try {
    sessionStorage.setItem(`th.allow.${surface}`, JSON.stringify(a));
  } catch {
    /* storage not available */
  }
}

/** The label of a form field: its accessible name, or the text of the label around it. */
function labelOf(el: HTMLElement): string | undefined {
  const a = el.getAttribute('aria-label');
  if (a) return a;
  if (el.id) {
    const l = document.querySelector(`label[for="${el.id}"]`);
    if (l?.textContent) return l.textContent.trim();
  }
  return el.closest('label')?.querySelector('span')?.textContent?.trim() || undefined;
}

export function AssistantDock() {
  const reg = useAssistReg();
  const [open, setOpen] = useState(() => {
    try {
      return localStorage.getItem('th.dock') !== '0';
    } catch {
      return true;
    }
  });
  const [tab, setTab] = useState<'happening' | 'ask'>('happening');
  const [focus, setFocus] = useState<string>();
  useEffect(() => {
    const on = (e: FocusEvent) => {
      const el = e.target as HTMLElement;
      if (!el || el.closest('.dock') || !['INPUT', 'SELECT', 'TEXTAREA'].includes(el.tagName)) return;
      const l = labelOf(el);
      if (l) setFocus(l);
    };
    document.addEventListener('focusin', on);
    return () => document.removeEventListener('focusin', on);
  }, []);
  useEffect(() => {
    document.body.classList.toggle('with-dock', open);
    try {
      localStorage.setItem('th.dock', open ? '1' : '0');
    } catch {
      /* storage not available */
    }
  }, [open]);

  if (!open) return <Button className="dock-tab" appearance="primary" icon={<SparkleRegular />} onClick={() => setOpen(true)} aria-label="Open the assistant">Assistant</Button>;
  return (
    <aside className="dock" aria-label="Assistant">
      <div className="dock-head">
        <TabList className="tabs small-tabs" size="small" selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)}>
          <Tab value="happening">What is happening</Tab>
          <Tab value="ask">Ask</Tab>
        </TabList>
        <Button appearance="subtle" size="small" icon={<DismissRegular />} onClick={() => setOpen(false)} aria-label="Close the assistant" />
      </div>
      <div className="dock-body">
        {tab === 'happening' ? <Happening reg={reg} focus={focus} /> : <Ask reg={reg} focus={focus} />}
      </div>
    </aside>
  );
}

// ------------------------------------------------------------------ what is happening

function Happening({ reg, focus }: { reg?: AssistReg; focus?: string }) {
  const { tenant } = useStudio();
  const [help, setHelp] = useState<any>();
  const [ex, setEx] = useState<any>();
  const [err, setErr] = useState<string>();
  useEffect(() => {
    if (!focus) return;
    get(`/api/help/fields?label=${encodeURIComponent(focus)}`).then(setHelp, () => setHelp(undefined));
  }, [focus]);
  const sig = useDebounced(JSON.stringify({ s: reg?.surface, d: reg?.doc, sel: reg?.selected, r: reg?.report }), 700);
  useEffect(() => {
    if (!reg) {
      setEx(undefined);
      return;
    }
    let live = true;
    post('/api/explain', { surface: reg.surface, doc: reg.doc, selected: reg.selected, report: reg.report }, reg.tenant ?? tenant)
      .then((r) => live && (setEx(r), setErr(undefined)))
      .catch((e) => live && setErr(e.message));
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sig, tenant]);

  return (
    <div className="stack" data-testid="happening">
      {help ? (
        <div className="dock-card" data-testid="field-help">
          <div className="small muted">The field you are in</div>
          <b>{help.label}</b>
          <p>{help.means}</p>
          {help.values && <ul className="dock-values">{help.values.map(([v, d]: [string, string]) => <li key={v}><code>{v}</code> {d}</li>)}</ul>}
          {help.example && <p className="small">For example: <code>{help.example}</code></p>}
          {help.mistake && <p className="small warn-text">Watch out: {help.mistake}</p>}
        </div>
      ) : <p className="small muted">Click into any field and its meaning shows here.</p>}
      {err && <div className="small fail-text">{err}</div>}
      {!reg && <p className="small muted">Nothing is being built on this page. Open an editor, the designer, a bank's configuration or the test bench, and what is happening is explained here as you work.</p>}
      {ex && (
        <div className="dock-card" data-testid="explanation">
          <b>{ex.title}</b>
          {ex.picture?.length > 0 && (
            <div className="dock-picture" aria-label="Picture">
              {ex.picture.map((n: any, i: number) => (
                <span key={i} className="dock-node-wrap">
                  {i > 0 && <span className="dock-arrow" aria-hidden>→</span>}
                  <span className={`dock-node t-${n.tone}`}><span>{n.label}</span>{n.sub && <small>{n.sub}</small>}</span>
                </span>
              ))}
            </div>
          )}
          <div className="dock-lines">{ex.lines.map((l: string, i: number) => <p key={i} className={l.startsWith('  ') ? 'indent' : ''}>{l}</p>)}</div>
          {ex.checklist?.length > 0 && (
            <>
              <div className="small muted">Before it can be published</div>
              <ul className="dock-check">{ex.checklist.map((c: any, i: number) => <li key={i} className={c.ok ? 'ok' : 'no'}><span aria-hidden>{c.ok ? '✓' : '✗'}</span> {c.text}</li>)}</ul>
            </>
          )}
          {ex.next && <p className="dock-next"><b>{ex.next}</b></p>}
        </div>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ ask

const convs = new Map<string, Conv>();

function Ask({ reg, focus }: { reg?: AssistReg; focus?: string }) {
  const { tenant, actor } = useStudio();
  const surface = reg?.surface ?? 'general';
  const key = `${tenant}|${surface}|${reg?.topic ?? reg?.doc?.name ?? reg?.kind ?? ''}`;
  const [conv, setConvState] = useState<Conv>(() => convs.get(key) ?? { messages: [], history: [], undone: [] });
  const [allow, setAllowState] = useState<string[]>(() => loadAllow(surface));
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [status, setStatus] = useState<any>();
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => { get('/api/ai/status').then(setStatus, () => setStatus(undefined)); }, []);
  useEffect(() => { setConvState(convs.get(key) ?? { messages: [], history: [], undone: [] }); setAllowState(loadAllow(surface)); }, [key, surface]);
  useEffect(() => { end.current?.scrollIntoView({ block: 'end' }); }, [conv, busy]);
  const setConv = (c: Conv) => { convs.set(key, c); setConvState(c); };
  const setAllow = (a: string[]) => { saveAllow(surface, a); setAllowState(a); };

  const call = async (messages: any[], extra: any = {}) => {
    if (!reg) return;
    setBusy(true);
    setError(undefined);
    try {
      const live = conv.history.filter((c) => !conv.undone.includes(c.id));
      const r = await post('/api/ai/agent', {
        context: { surface, kind: reg.kind, doc: reg.doc, focus, selected: reg.selected, report: reg.report, actor },
        messages, allow, history: live, ...extra,
      }, reg.tenant ?? tenant);
      if ((r.changes?.length || r.undone?.length) && reg.setDoc) reg.setDoc(r.doc);
      setAllow(r.allow ?? allow);
      setConv({ messages: r.messages, history: [...conv.history, ...(r.changes ?? [])], undone: [...conv.undone, ...(r.undone ?? [])], pending: r.pending, flagged: [...(conv.flagged ?? []), ...(r.flagged ?? [])] });
    } catch (e: any) {
      setError(refusal(e));
    } finally {
      setBusy(false);
    }
  };
  const send = () => {
    const t = text.trim();
    if (!t || busy) return;
    setText('');
    void call([...conv.messages, { role: 'user', content: t }]);
  };
  const decide = (decision: 'once' | 'always' | 'deny') => {
    if (!conv.pending) return;
    void call(conv.messages, { pending: conv.pending, decision });
  };
  /** The designer undoes a change the assistant made. */
  const undo = (c: Change) => {
    if (!reg?.setDoc) return;
    if (JSON.stringify(reg.doc) !== JSON.stringify(c.after) && !window.confirm(`The document changed after ${c.id}. Undo it anyway? Changes made since will be lost.`)) return;
    reg.setDoc(c.before);
    const later = conv.history.filter((x) => conv.history.indexOf(x) >= conv.history.indexOf(c)).map((x) => x.id);
    setConv({ ...conv, undone: [...new Set([...conv.undone, ...later])] });
  };

  if (!reg) return <p className="small muted">Open an editor, the designer, a bank's configuration or the test bench to ask about what you are building.</p>;
  return (
    <div className="ask" data-testid="ask">
      {status && !status.configured && <Alert tone="warn" className="small" role="status">The assistant has no model credentials, so it cannot answer. {status.how} What is happening still works.</Alert>}
      {status?.how === 'replay' && <Alert tone="info" className="small">Replaying a recorded conversation, not a live model.</Alert>}
      <div className="ask-log">
        {conv.messages.map((m, i) => <Message key={i} m={m} />)}
        {conv.pending && (
          <div className="dock-card pending" data-testid="pending">
            <b>The assistant wants to: {conv.pending.summary}</b>
            {conv.pending.preview.changed?.length ? <p className="small">It changes: {conv.pending.preview.changed.join(', ')}</p> : null}
            {conv.pending.allowAlways === false && <p className="small muted" data-testid="ask-every-time">You are asked about this every time: a sample goes into the change set, for its checker.</p>}
            {conv.pending.preview.check && <pre className="code small">{conv.pending.preview.check}</pre>}
            {conv.pending.preview.detail && <details><summary className="small">Show the whole {conv.pending.tool === 'edit_document' ? 'new document' : 'content'}</summary><pre className="code small">{conv.pending.preview.detail}</pre></details>}
            <div className="row-actions">
              <Button appearance="primary" disabled={busy} onClick={() => decide('once')}>Allow once</Button>
              {conv.pending.allowAlways !== false && <Button disabled={busy} onClick={() => decide('always')}>Allow always</Button>}
              <Button className="danger" disabled={busy} onClick={() => decide('deny')}>Deny</Button>
            </div>
          </div>
        )}
        {(conv.flagged ?? []).map((f, i) => (
          <Alert key={`${f.toolUseId}-${i}`} tone="warn" className="small" role="status" data-testid="instruction-flag">
            What the assistant read through {f.tool} holds text that looks like an instruction to it: <q>{f.excerpt}</q>. It treats it as data and does not follow it. Check where it came from.
          </Alert>
        ))}
        {busy && <p className="small muted">Working…</p>}
        {error && <div className="small fail-text" role="alert">{error}</div>}
        <div ref={end} />
      </div>
      {conv.history.length > 0 && (
        <details className="dock-changes" open>
          <summary className="small">Changes the assistant made ({conv.history.length})</summary>
          <ul>
            {conv.history.map((c) => (
              <li key={c.id} className={conv.undone.includes(c.id) ? 'undone' : ''}>
                <code>{c.id}</code> {c.summary} {conv.undone.includes(c.id) ? <span className="muted small">undone</span> : <Link as="button" className="small" onClick={() => undo(c)}>Undo</Link>}
              </li>
            ))}
          </ul>
        </details>
      )}
      {allow.length > 0 && <p className="small muted">Allowed always here: {allow.join(', ')}. <Link as="button" className="small" onClick={() => setAllow([])}>Ask every time again</Link></p>}
      <div className="chips">
        {suggestions(reg).map((s) => <Button size="small" key={s} className="chip-btn" onClick={() => setText(s)}>{s}</Button>)}
      </div>
      <Textarea className="code-input" rows={3} value={text} onChange={(e) => setText(e.target.value)} aria-label="Ask the assistant"
        placeholder={`Ask about this ${reg.doc?.kind ?? reg.surface}, or ask it to build something`}
        onKeyDown={(e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) send(); }} />
      <div className="row-actions">
        <Button appearance="primary" disabled={busy || !text.trim() || !!conv.pending} onClick={send}>Send</Button>
        {conv.messages.length > 0 && <Link as="button" className="small" onClick={() => setConv({ messages: [], history: conv.history, undone: conv.undone })}>New conversation</Link>}
      </div>
      <p className="small muted">It sees this page and the document. It asks before every change and never publishes. Never type personal data.</p>
    </div>
  );
}

/** A message's text. The assistant's replies use a little markdown; the designer's text is shown as typed. */
function Bubble({ role, text }: { role: string; text: string }) {
  return <div className={`bubble ${role}`}>{role === 'assistant' ? <Markdown text={text} /> : text}</div>;
}

function Message({ m }: { m: any }) {
  if (typeof m.content === 'string') return <Bubble role={m.role} text={m.content} />;
  const parts = (m.content ?? []).map((b: any, i: number) => {
    if (b.type === 'text' && !b.text.startsWith('<context>')) return <Bubble key={i} role={m.role} text={b.text} />;
    if (b.type === 'tool_use') return <div key={i} className="activity small muted">• {(ACTIVITY[b.name] ?? (() => b.name))(b.input ?? {})}</div>;
    if (b.type === 'tool_result' && b.is_error && /did not allow/.test(String(b.content))) return <div key={i} className="activity small muted">• You did not allow it</div>;
    return null;
  });
  return <>{parts}</>;
}
