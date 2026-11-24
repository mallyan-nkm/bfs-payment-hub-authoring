import { useState } from 'react';
import { DataTabs } from './Data';
import { get, q } from '../api';
import { Alert, ArtefactLink, Badge, ErrorBox, Loading, Provenance, Section, useDebounced, useLoad } from '../ui';
import { DispatchTable } from './Artefact';
import { Button, Link, SearchBox, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';

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
        <SearchBox className="grow" value={text} onChange={(_, d) => setText(d.value)} placeholder="For example payment.instructedAmount, salary or AM04" aria-label="Search the data" />
      </div>
      {data.data?.parts && (
        <p className="small">
          Context parts:{' '}
          {data.data.parts.map((p: string) => <Button size="small" key={p} className="chip-btn mono" onClick={() => setText(p)}>{p}</Button>)}
        </p>
      )}
      <ErrorBox error={data.error} />
      {data.loading && !data.data ? <Loading /> : data.data && (
        <>
          <Section title="Paths" count={data.data.paths.length}>
            {data.data.paths.length === 0 ? <p className="muted">No block reads or writes a path that matches.</p> : (
              <div className="table-wrap">
                <Table size="small" className="grid">
                  <TableHeader><TableRow><TableHeaderCell>Path</TableHeaderCell><TableHeaderCell>Readers</TableHeaderCell><TableHeaderCell>Writers</TableHeaderCell><TableHeaderCell>Extension</TableHeaderCell></TableRow></TableHeader>
                  <TableBody>
                    {data.data.paths.map((p: any) => (
                      <TableRow key={p.path}>
                        <TableCell className="mono break"><b>{p.path}</b></TableCell>
                        <TableCell>{p.readers.length ? <ul className="plain">{p.readers.map((r: string) => <li key={r}><Reader r={r} /></li>)}</ul> : <span className="muted">none</span>}</TableCell>
                        <TableCell>{p.writers.length ? <ul className="plain">{p.writers.map((r: string) => <li key={r}><Reader r={r} /></li>)}</ul> : <span className="muted">none</span>}</TableCell>
                        <TableCell>
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
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            )}
          </Section>
          <Section title="Reason codes" count={data.data.reasonCodes.length}>
            {data.data.reasonCodes.length === 0 ? <p className="muted">No reason code matches. Search a code such as AM04, or clear the box to list all.</p> : (
              <Table size="small" className="grid compact">
                <TableHeader><TableRow><TableHeaderCell>Code</TableHeaderCell><TableHeaderCell>Rules that yield it</TableHeaderCell></TableRow></TableHeader>
                <TableBody>{data.data.reasonCodes.map((c: any) => <TableRow key={c.code}><TableCell className="mono">{c.code}</TableCell><TableCell><span className="chips">{c.rules.map((r: string) => <ArtefactLink key={r} name={r} />)}</span></TableCell></TableRow>)}</TableBody>
              </Table>
            )}
          </Section>
        </>
      )}

      <Section title="Scheme message catalogues">
        <ErrorBox error={schemes.error} />
        {schemes.data && (
          <Table size="small" className="grid">
            <TableHeader><TableRow><TableHeaderCell>Scheme</TableHeaderCell><TableHeaderCell>Title</TableHeaderCell><TableHeaderCell>Release</TableHeaderCell><TableHeaderCell>Wire</TableHeaderCell><TableHeaderCell>Messages</TableHeaderCell><TableHeaderCell>Planned</TableHeaderCell><TableHeaderCell>Provenance</TableHeaderCell></TableRow></TableHeader>
            <TableBody>
              {schemes.data.map((s) => (
                <TableRow key={s.scope} className={scheme === s.scope ? 'clickable sel' : 'clickable'} onClick={() => setScheme(s.scope)}>
                  <TableCell className="mono"><Link as="button">{s.scope}</Link></TableCell>
                  <TableCell>{s.title}</TableCell><TableCell className="mono">{s.release}</TableCell><TableCell>{s.wire}</TableCell><TableCell>{s.messages}</TableCell>
                  <TableCell>{s.planned ? <Badge tone="warn">{s.planned} planned</Badge> : <Badge tone="pass">none</Badge>}</TableCell>
                  <TableCell><Provenance status={s.provenance} /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Section>
      {scheme && detail.loading && <Loading what={scheme} />}
      <ErrorBox error={detail.error} />
      {detail.data && (
        <>
          {detail.data.scheme?.provenance?.status === 'provisional' && (
            <Alert tone="prov"><b>Unconfirmed.</b> {detail.data.scheme.provenance.note} {detail.data.scheme.provenance.clauses?.length ? `Sources: ${detail.data.scheme.provenance.clauses.join('; ')}.` : ''}</Alert>
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
