import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import type { Issue, Meta, User } from './api';

// Shared building blocks: the studio context, data loading, badges, issues and small tables.

export interface StudioCtx {
  tenant: string;
  actor: string;
  meta?: Meta;
  /** The studio's users in this tenant (GET /api/users). */
  users: User[];
  setActor: (id: string) => void;
  go: (path: string) => void;
}
export const Studio = createContext<StudioCtx>({ tenant: 'owner', actor: 'shared.designer', users: [], setActor: () => {}, go: () => {} });
export const useStudio = () => useContext(Studio);

/** Load data from the API; reloads when deps change. */
export function useLoad<T>(fn: () => Promise<T>, deps: any[]): { data?: T; error?: string; loading: boolean; reload: () => void } {
  const [state, setState] = useState<{ data?: T; error?: string; loading: boolean }>({ loading: true });
  const [n, setN] = useState(0);
  useEffect(() => {
    let live = true;
    setState((s) => ({ ...s, loading: true, error: undefined }));
    fn().then(
      (data) => live && setState({ data, loading: false }),
      (e) => live && setState({ error: String(e?.message ?? e), loading: false }),
    );
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, n]);
  return { ...state, reload: useCallback(() => setN((x) => x + 1), []) };
}

/** Run an action with busy and error state. */
export function useAction<A extends any[], T>(fn: (...a: A) => Promise<T>) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [result, setResult] = useState<T>();
  const run = async (...a: A) => {
    setBusy(true);
    setError(undefined);
    try {
      const r = await fn(...a);
      setResult(r);
      return r;
    } catch (e: any) {
      setError(String(e?.message ?? e));
      return undefined;
    } finally {
      setBusy(false);
    }
  };
  return { run, busy, error, result, setResult, setError };
}

export function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  const t = useRef<any>();
  useEffect(() => {
    clearTimeout(t.current);
    t.current = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t.current);
  }, [value, ms]);
  return v;
}

export function Loading({ what }: { what?: string }) {
  return <p className="muted">Loading{what ? ` ${what}` : ''}…</p>;
}

export function ErrorBox({ error }: { error?: string }) {
  if (!error) return null;
  return <div className="alert fail" role="alert">{error}</div>;
}

export function Badge({ tone, children, title }: { tone?: 'pass' | 'fail' | 'warn' | 'prov' | 'info' | 'muted'; children: ReactNode; title?: string }) {
  return <span className={`badge ${tone ?? 'muted'}`} title={title}>{children}</span>;
}

export function PassFail({ passed }: { passed: boolean | undefined }) {
  if (passed === undefined) return <Badge>not run</Badge>;
  return passed ? <Badge tone="pass">pass</Badge> : <Badge tone="fail">fail</Badge>;
}

export function Provenance({ status }: { status?: string }) {
  if (!status) return null;
  if (status === 'provisional') return <Badge tone="prov" title="Written from public sources. Not confirmed against the scheme rulebook.">provisional, unconfirmed</Badge>;
  if (status === 'confirmed') return <Badge tone="pass">confirmed</Badge>;
  return <Badge>{status}</Badge>;
}

export function StatusBadge({ status }: { status?: string }) {
  if (!status) return null;
  return <Badge tone={status === 'published' ? 'info' : status === 'draft' ? 'warn' : 'muted'}>{status}</Badge>;
}

export function Section({ title, children, actions, count }: { title: ReactNode; children: ReactNode; actions?: ReactNode; count?: number }) {
  return (
    <section className="panel">
      <header className="panel-head">
        <h3>
          {title}
          {count !== undefined && <span className="count">{count}</span>}
        </h3>
        {actions && <div className="actions">{actions}</div>}
      </header>
      <div className="panel-body">{children}</div>
    </section>
  );
}

export function Issues({ issues, onStep, empty }: { issues?: Issue[]; onStep?: (step: string) => void; empty?: string }) {
  const { meta } = useStudio();
  if (!issues) return null;
  if (!issues.length) return <p className="ok-line">{empty ?? 'No issues.'}</p>;
  return (
    <ul className="issues">
      {issues.map((i, n) => (
        <li key={n} className={i.severity === 'error' ? 'sev-error' : 'sev-warning'}>
          <Badge tone={i.severity === 'error' ? 'fail' : 'warn'} title={meta?.checks?.[i.code]}>{i.code}</Badge>
          {i.step && (
            <button className="link step-ref" onClick={() => onStep?.(i.step!)} title="Step">
              {i.step}
            </button>
          )}
          <span className="msg">{i.message}</span>
          {i.key && <span className="muted small"> {i.key}</span>}
        </li>
      ))}
    </ul>
  );
}

export function Json({ value, max }: { value: any; max?: number }) {
  const s = value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value, null, 2);
  return <pre className="code">{max && s.length > max ? s.slice(0, max) + '\n…' : s}</pre>;
}

/** A value shown inline: short JSON on one line. */
export function Val({ v }: { v: any }) {
  if (v === undefined) return <span className="muted">none</span>;
  if (v === null) return <span className="muted">null</span>;
  if (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean') return <code>{String(v)}</code>;
  const s = JSON.stringify(v);
  return s.length > 80 ? (
    <details className="inline">
      <summary><code>{s.slice(0, 60)}…</code></summary>
      <Json value={v} />
    </details>
  ) : (
    <code>{s}</code>
  );
}

export function List({ items, render, empty }: { items?: any[]; render?: (x: any, i: number) => ReactNode; empty?: string }) {
  if (!items || !items.length) return <p className="muted">{empty ?? 'None.'}</p>;
  return (
    <ul className="plain">
      {items.map((x, i) => (
        <li key={i}>{render ? render(x, i) : String(x)}</li>
      ))}
    </ul>
  );
}

export function ArtefactLink({ name, release, keyOf }: { name: string; release?: string; keyOf?: string }) {
  const { go } = useStudio();
  const href = `#/artefact/${encodeURIComponent(name)}${keyOf ? `?key=${encodeURIComponent(keyOf)}` : release ? `?release=${encodeURIComponent(release)}` : ''}`;
  return (
    <a href={href} onClick={(e) => { e.preventDefault(); go(href.slice(1)); }} className="mono">
      {name}
      {release ? <span className="muted">@{release}</span> : null}
    </a>
  );
}

export function scopeText(s: any): string {
  if (s === undefined || s === null) return '';
  return typeof s === 'string' ? s : 'other';
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: ReactNode }[]; value: T; onChange: (t: T) => void }) {
  return (
    <div className="tabs" role="tablist">
      {tabs.map((t) => (
        <button key={t.id} role="tab" aria-selected={t.id === value} className={t.id === value ? 'tab on' : 'tab'} onClick={() => onChange(t.id)}>
          {t.label}
        </button>
      ))}
    </div>
  );
}

export function KV({ rows }: { rows: [ReactNode, ReactNode][] }) {
  return (
    <dl className="kv">
      {rows.map(([k, v], i) => (
        <div key={i} className="kv-row">
          <dt>{k}</dt>
          <dd>{v}</dd>
        </div>
      ))}
    </dl>
  );
}

export function fmtMs(ms: number | undefined): string {
  if (ms === undefined || ms === null) return '';
  if (ms === 0) return '0s';
  const neg = ms < 0;
  let s = Math.abs(ms) / 1000;
  const d = Math.floor(s / 86400); s -= d * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60); s -= m * 60;
  const parts = [d && `${d}d`, h && `${h}h`, m && `${m}m`, s && `${Math.round(s * 1000) / 1000}s`].filter(Boolean);
  return (neg ? '-' : '') + parts.join(' ');
}
