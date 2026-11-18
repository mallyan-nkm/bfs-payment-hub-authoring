import { useState } from 'react';
import { ApiError, get, post, refusal, type Gate } from '../api';
import { Badge, ErrorBox, Json, KV, List, Loading, Section, useLoad, useStudio } from '../ui';
import { DraftActions, Gates, useCatalogue } from '../authoring';

// The AI authoring assistant (C16, F-20, F-21). It drafts a contract, rule, script or simulator
// from scheme text. Every draft runs through the same gates as any other; the assistant never
// publishes. Without model credentials the studio says so and drafts nothing.

type Kind = 'activity' | 'rule' | 'script' | 'simulator';

const HINT: Record<Kind, string> = {
  activity: 'Draft an activity contract: reads, writes, outcomes, errors, effect, ports and policy.',
  rule: 'Draft rules with examples. Each rule cites its clause; what the text does not say comes back as a question.',
  script: 'Draft the script-behind for an activity, in TypeScript or Python, using only the injected interface and the generated types.',
  simulator: 'Draft a simulator for a port with the eight standard scenarios.',
};

export function Assistant() {
  const status = useLoad(() => get<{ configured: boolean; how: string }>('/api/ai/status'), []);
  const [kind, setKind] = useState<Kind>('rule');
  const [instruction, setInstruction] = useState('');
  const [sourceText, setSourceText] = useState('');
  const [target, setTarget] = useState('');
  const [language, setLanguage] = useState<'typescript' | 'python'>('typescript');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [draft, setDraft] = useState<any>();
  const [draftKey, setDraftKey] = useState<string>();
  const [templateId, setTemplateId] = useState('');
  const templates = useLoad(() => get<any[]>('/api/ai/templates'), []);
  const forKind = (templates.data ?? []).filter((t) => t.kind === kind);
  const chosen = forKind.find((t) => t.id === templateId);
  // The parts of the instruction still to fill: anything in <angle brackets>.
  // A comparison such as "amount < 10 and total > 5" has spaces inside the brackets, so it is not a blank.
  const blanks = [...instruction.matchAll(/<(?=\S)([^<>\n]{1,80})(?<=\S)>/g)].map((m) => m[1]);
  const applyTemplate = (id: string) => {
    setTemplateId(id);
    const t = forKind.find((x) => x.id === id);
    if (!t) return;
    setInstruction(t.instruction);
    if (t.language) setLanguage(t.language);
  };
  const activities = useCatalogue('activity');
  const ports = useCatalogue('port');
  const targets = kind === 'script' ? activities : kind === 'simulator' ? ports : [];
  const configured = status.data?.configured;

  const ask = async () => {
    setBusy(true);
    setError(undefined);
    setDraft(undefined);
    try {
      setDraft(await post('/api/ai/draft', { kind, instruction, sourceText: sourceText || undefined, target: target || undefined, language: kind === 'script' ? language : undefined }));
    } catch (e: any) {
      setError(e instanceof ApiError && e.status === 503 ? e.message : refusal(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="view">
      <div className="view-head">
        <h2>AI assistant</h2>
        <p className="muted">Ask for a draft from scheme text. You judge it by behaviour: it runs through the gates, schema, static checks, type check, tests and determinism, and the first red gate is shown. Publish stays with maker-checker.</p>
      </div>
      <Section title="Status">
        {status.loading && !status.data ? <Loading /> : status.error ? <ErrorBox error={status.error} /> : configured ? (
          <p><Badge tone="pass">connected</Badge> The assistant has model credentials on the studio server.</p>
        ) : (
          <div className="alert warn" role="status">
            <b>The assistant has no model credentials.</b> {status.data?.how} No draft is made, and none is invented in its place.
          </div>
        )}
      </Section>
      <div className="cols">
        <div className="col">
          <Section title="Ask for a draft">
            <div className="form-grid">
              <label className="field">
                <span>Kind</span>
                <select value={kind} onChange={(e) => { setKind(e.target.value as Kind); setTarget(''); setTemplateId(''); }} aria-label="Draft kind">
                  <option value="activity">activity contract</option>
                  <option value="rule">rule</option>
                  <option value="script">script-behind</option>
                  <option value="simulator">simulator</option>
                </select>
              </label>
              {(kind === 'script' || kind === 'simulator') && (
                <label className="field wide">
                  <span>{kind === 'script' ? 'Activity it implements' : 'Port it plays'}</span>
                  <select value={target} onChange={(e) => setTarget(e.target.value)} aria-label="Target">
                    <option value="">Choose</option>
                    {targets.filter((t) => t.status === 'published').map((t) => <option key={t.key} value={t.name}>{t.name}</option>)}
                  </select>
                </label>
              )}
              {kind === 'script' && (
                <label className="field">
                  <span>Language</span>
                  <select value={language} onChange={(e) => setLanguage(e.target.value as any)} aria-label="Language">
                    <option value="typescript">TypeScript</option>
                    <option value="python">Python</option>
                  </select>
                </label>
              )}
            </div>
            <p className="muted small">{HINT[kind]}</p>
            <label className="field">
              <span>Start from a template</span>
              <select value={templateId} onChange={(e) => applyTemplate(e.target.value)} aria-label="Prompt template">
                <option value="">None: write the instruction yourself</option>
                {forKind.map((t) => <option key={t.id} value={t.id}>{t.title}: {t.use}</option>)}
              </select>
            </label>
            {chosen && <p className="muted small">Fill in every part in &lt;angle brackets&gt;, and remove the choices you do not want. Source text: {chosen.sourceHint}</p>}
            <label className="field">
              <span>Instruction</span>
              <textarea className="code-input" rows={chosen ? 8 : 3} value={instruction} onChange={(e) => setInstruction(e.target.value)} placeholder="What you want, in a sentence or two" aria-label="Instruction" />
            </label>
            <label className="field">
              <span>Source text</span>
              <textarea className="code-input" rows={7} value={sourceText} onChange={(e) => setSourceText(e.target.value)} placeholder={chosen?.sourceHint ?? 'Paste a public rulebook clause or guide extract. Use synthetic values; never paste personal data.'} aria-label="Source text" />
            </label>
            {blanks.length > 0 && <div className="alert warn small" role="status">Still to fill: {blanks.map((b) => `<${b}>`).join(', ')}</div>}
            <ErrorBox error={error} />
            <div className="row-actions">
              <button className="primary" disabled={busy || !instruction.trim() || blanks.length > 0 || ((kind === 'script' || kind === 'simulator') && !target)} onClick={ask}>{busy ? 'Drafting…' : 'Ask for a draft'}</button>
              {!configured && status.data && <span className="muted small">The server will answer that it has no credentials.</span>}
            </div>
          </Section>
        </div>
        <div className="col side">
          <Section title="What comes back">
            <List items={['The drafted document, and a script when one was asked for', 'Tests drafted with it', 'Questions: what the source does not say, not guessed', 'Clauses: where each part came from', 'Provenance: model, prompt reference and inputs', 'The gates, with the first red one']} />
          </Section>
        </div>
      </div>
      {draft && <DraftResult draft={draft} draftKey={draftKey} setDraftKey={setDraftKey} />}
    </div>
  );
}

function DraftResult({ draft, draftKey, setDraftKey }: { draft: any; draftKey?: string; setDraftKey: (k?: string) => void }) {
  const { tenant } = useStudio();
  const gates = { result: { gates: draft.gates as Gate[], passed: draft.passed, firstRed: draft.firstRed }, busy: false };
  void tenant;
  return (
    <div className="cols">
      <div className="col">
        <Section title="Draft" actions={draft.passed ? <Badge tone="pass">every gate green</Badge> : <Badge tone="fail">red at {draft.firstRed?.gate}</Badge>}>
          <KV rows={[
            ['Name', <code>{draft.doc?.name ?? '(none)'}</code>],
            ['Served by', draft.servedBy ?? <span className="muted">not given</span>],
            ['Stop reason', draft.stopReason ?? <span className="muted">not given</span>],
          ]} />
          <pre className="code tall">{draft.yaml}</pre>
          {draft.script && (
            <>
              <h4>Script ({draft.script.language})</h4>
              <pre className="code">{draft.script.source}</pre>
            </>
          )}
          {draft.tests?.length > 0 && (
            <details>
              <summary>Tests drafted with it ({draft.tests.length})</summary>
              <Json value={draft.tests} />
            </details>
          )}
        </Section>
        <Section title="Questions" count={draft.questions?.length}>
          <List items={draft.questions} empty="No open question." />
        </Section>
        <Section title="Clauses" count={draft.clauses?.length}>
          <List items={draft.clauses} empty="No clause cited." />
        </Section>
      </div>
      <div className="col side">
        <Gates state={gates} />
        <Section title="Provenance">
          <Json value={draft.provenance} />
        </Section>
        {draft.doc && (
          <Section title="Save as a draft">
            <DraftActions doc={draft.doc} draftKey={draftKey} setDraftKey={setDraftKey} />
          </Section>
        )}
      </div>
    </div>
  );
}
