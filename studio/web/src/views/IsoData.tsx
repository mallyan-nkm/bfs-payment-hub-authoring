import { useState } from 'react';
import { DataTabs } from './Data';
import { get, q } from '../api';
import { ArtefactLink, Badge, ErrorBox, Loading, Provenance, Section, useDebounced, useLoad } from '../ui';
import { DispatchTable } from './Artefact';

// The ISO data browser (F-03) and the scheme message catalogues (F-04).

function Reader({ r }: { r: string }) {
  const m = /^(\S+)( \(via (.+)\))?$/.exec(r);
  if (!m) return <span>{r}</span>;
  return (
    <span className="nowrap">
      <ArtefactLink name={m[1]} />
      {m[3] && <span className="muted small"> via {m[3]}</span>}
    </span>
  );
}

function PathsTab({ initial }: { initial?: string }) {
  const [text, setText] = useState(initial || 'payment.instructedAmount');
  const dq = useDebounced(text, 300);
  const data = useLoad(() => get(`/api/data${q({ q: dq })}`), [dq]);
  const schemes = useLoad(() => get<any[]>('/api/schemes'), []);
  const [scheme, setScheme] = useState<string>();
  const detail = useLoad(() => (scheme ? get(`/api/schemes/${encodeURIComponent(scheme)}`) : Promise.resolve(undefined)), [scheme]);

  return (
    <div>
      <p className="muted">Search a canonical context path, an extension field or a reason code. See which blocks read and write it.</p>
      <div className="toolbar">
        <input type="search" className="grow" value={text} onChange={(e) => setText(e.target.value)} placeholder="For example payment.instructedAmount, salary or AM04" aria-label="Search the data" />
      </div>
      {data.data?.parts && (
        <p className="small">
          Context parts:{' '}
          {data.data.parts.map((p: string) => <button key={p} className="chip-btn mono" onClick={() => setText(p)}>{p}</button>)}
        </p>
      )}
      <ErrorBox error={data.error} />
      {data.loading && !data.data ? <Loading /> : data.data && (
        <>
          <Section title="Paths" count={data.data.paths.length}>
            {data.data.paths.length === 0 ? <p className="muted">No block reads or writes a path that matches.</p> : (
              <div className="table-wrap">
                <table className="grid">
                  <thead><tr><th>Path</th><th>Readers</th><th>Writers</th><th>Extension</th></tr></thead>
                  <tbody>
                    {data.data.paths.map((p: any) => (
                      <tr key={p.path}>
                        <td className="mono break"><b>{p.path}</b></td>
                        <td>{p.readers.length ? <ul className="plain">{p.readers.map((r: string) => <li key={r}><Reader r={r} /></li>)}</ul> : <span className="muted">none</span>}</td>
                        <td>{p.writers.length ? <ul className="plain">{p.writers.map((r: string) => <li key={r}><Reader r={r} /></li>)}</ul> : <span className="muted">none</span>}</td>
                        <td>
                          {p.extension ? (
                            <div className="small">
                              <div><ArtefactLink name={p.extension.extension} /></div>
                              <div>{p.extension.description}</div>
                              <div>
                                {p.extension.isoType && <Badge>{p.extension.isoType}</Badge>} {p.extension.level && <Badge>level {p.extension.level}</Badge>}{' '}
                                {p.extension.source && <Badge>source {p.extension.source}</Badge>}{' '}
                                {p.extension.personalData ? <Badge tone="warn" title="Personal data. Must not appear in workflow state, logs, tests, samples or prompts.">personal data</Badge> : <Badge>not personal</Badge>}
                              </div>
                            </div>
                          ) : <span className="muted">canonical</span>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Section>
          <Section title="Reason codes" count={data.data.reasonCodes.length}>
            {data.data.reasonCodes.length === 0 ? <p className="muted">No reason code matches. Search a code such as AM04, or clear the box to list all.</p> : (
              <table className="grid compact">
                <thead><tr><th>Code</th><th>Rules that yield it</th></tr></thead>
                <tbody>{data.data.reasonCodes.map((c: any) => <tr key={c.code}><td className="mono">{c.code}</td><td><span className="chips">{c.rules.map((r: string) => <ArtefactLink key={r} name={r} />)}</span></td></tr>)}</tbody>
              </table>
            )}
          </Section>
        </>
      )}

      <Section title="Scheme message catalogues">
        <ErrorBox error={schemes.error} />
        {schemes.data && (
          <table className="grid">
            <thead><tr><th>Scheme</th><th>Title</th><th>Release</th><th>Wire</th><th>Messages</th><th>Planned</th><th>Provenance</th></tr></thead>
            <tbody>
              {schemes.data.map((s) => (
                <tr key={s.scope} className={scheme === s.scope ? 'clickable sel' : 'clickable'} onClick={() => setScheme(s.scope)}>
                  <td className="mono"><button className="link">{s.scope}</button></td>
                  <td>{s.title}</td><td className="mono">{s.release}</td><td>{s.wire}</td><td>{s.messages}</td>
                  <td>{s.planned ? <Badge tone="warn">{s.planned} planned</Badge> : <Badge tone="pass">none</Badge>}</td>
                  <td><Provenance status={s.provenance} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Section>
      {scheme && detail.loading && <Loading what={scheme} />}
      <ErrorBox error={detail.error} />
      {detail.data && (
        <>
          {detail.data.scheme?.provenance?.status === 'provisional' && (
            <div className="alert prov"><b>Unconfirmed.</b> {detail.data.scheme.provenance.note} {detail.data.scheme.provenance.clauses?.length ? `Sources: ${detail.data.scheme.provenance.clauses.join('; ')}.` : ''}</div>
          )}
          <DispatchTable rows={detail.data.dispatch} />
          <details className="panel pad">
            <summary>Scheme manifest YAML</summary>
            <pre className="code tall">{detail.data.yaml}</pre>
          </details>
        </>
      )}
    </div>
  );
}

export function IsoData({ query }: { query: URLSearchParams }) {
  return <DataTabs query={query} paths={<PathsTab key={query.get('q') ?? ''} initial={query.get('q') ?? undefined} />} />;
}
