import { useMemo, useState } from 'react';
import { get, q, type Summary } from '../api';
import { ArtefactLink, Badge, ErrorBox, Loading, Provenance, StatusBadge, scopeText, useDebounced, useLoad, useStudio } from '../ui';

interface CatalogueResult { items: Summary[]; scopes: any[]; kinds: string[]; partial?: string }

/**
 * Fetch the catalogue. When the unfiltered call fails on the server (one document kind can break
 * the sort), fall back to one call per kind and merge, and say so.
 */
async function fetchCatalogue(params: Record<string, string>, kinds: string[]): Promise<CatalogueResult> {
  try {
    return await get<CatalogueResult>(`/api/catalogue${q(params)}`);
  } catch (e: any) {
    if (params.kind || !kinds.length) throw e;
    const parts = await Promise.all(kinds.map((k) => get<CatalogueResult>(`/api/catalogue${q({ ...params, kind: k })}`).catch(() => undefined)));
    const failed = kinds.filter((_, i) => !parts[i]);
    const items = parts.flatMap((p) => p?.items ?? []);
    items.sort((a, b) => scopeText(a.scope).localeCompare(scopeText(b.scope)) || a.kind.localeCompare(b.kind) || a.name.localeCompare(b.name));
    return {
      items,
      scopes: [...new Set(items.map((i) => scopeText(i.scope)))].sort(),
      kinds: [...new Set(items.map((i) => i.kind))].sort(),
      partial: `The full catalogue call failed (${e.message}); results were merged per kind${failed.length ? `, and these kinds failed: ${failed.join(', ')}` : ''}.`,
    };
  }
}

const HIDDEN = ['inventory', 'conformance-kit'];

export function Catalogue({ query }: { query: URLSearchParams }) {
  const { meta, tenant, go } = useStudio();
  const [text, setText] = useState(query.get('q') ?? '');
  const [scope, setScope] = useState(query.get('scope') ?? '');
  const [kind, setKind] = useState(query.get('kind') ?? '');
  const [type, setType] = useState(query.get('type') ?? '');
  const dq = useDebounced(text, 250);
  const kinds = useMemo(() => Object.keys(meta?.counts ?? {}).filter((k) => !HIDDEN.includes(k)).sort(), [meta]);

  const all = useLoad(() => (meta ? fetchCatalogue({}, kinds) : Promise.resolve(undefined)), [meta, tenant]);
  const res = useLoad(() => (meta ? fetchCatalogue({ q: dq, scope, kind, type }, kinds) : Promise.resolve(undefined)), [meta, tenant, dq, scope, kind, type]);

  const opts = useMemo(() => {
    const items = all.data?.items ?? [];
    return {
      scopes: [...new Set(items.map((i) => scopeText(i.scope)).filter((s) => s && s !== 'other'))].sort(),
      kinds: [...new Set(items.map((i) => i.kind))].sort(),
      types: [...new Set(items.filter((i) => !kind || i.kind === kind).map((i) => i.type).filter(Boolean))].sort(),
    };
  }, [all.data, kind]);

  const setUrl = (over: Record<string, string>) => {
    const p = { q: text, scope, kind, type, ...over };
    window.history.replaceState(null, '', `#/catalogue${q(p)}`);
  };

  return (
    <div className="view">
      <div className="view-head">
        <h2>Catalogue</h2>
        <p className="muted">Every artefact this tenant may see: shared content and its own. Search by name, title, description or a path read or written.</p>
      </div>
      <div className="toolbar">
        <input type="search" className="grow" placeholder="Search, for example iban or payment.creditorAccount" value={text} aria-label="Search" onChange={(e) => { setText(e.target.value); setUrl({ q: e.target.value }); }} />
        <select value={scope} onChange={(e) => { setScope(e.target.value); setUrl({ scope: e.target.value }); }} aria-label="Scope">
          <option value="">Any scope</option>
          {opts.scopes.map((s) => <option key={s}>{s}</option>)}
        </select>
        <select value={kind} onChange={(e) => { setKind(e.target.value); setType(''); setUrl({ kind: e.target.value, type: '' }); }} aria-label="Kind">
          <option value="">Any kind</option>
          {opts.kinds.map((s) => <option key={s}>{s}</option>)}
        </select>
        <select value={type} onChange={(e) => { setType(e.target.value); setUrl({ type: e.target.value }); }} aria-label="Type">
          <option value="">Any type</option>
          {opts.types.map((s) => <option key={s}>{s}</option>)}
        </select>
      </div>
      <ErrorBox error={res.error} />
      {res.data?.partial && <div className="alert warn">{res.data.partial}</div>}
      {res.loading && !res.data ? <Loading what="the catalogue" /> : res.data && (
        <>
          <p className="muted small">{res.data.items.length} artefact{res.data.items.length === 1 ? '' : 's'}</p>
          <div className="table-wrap">
            <table className="grid">
              <thead>
                <tr><th>Name</th><th>Title</th><th>Scope</th><th>Kind</th><th>Type</th><th>Release</th><th>Status</th></tr>
              </thead>
              <tbody>
                {res.data.items.map((i) => (
                  <tr key={i.key} className="clickable" onClick={(e) => { if ((e.target as HTMLElement).tagName !== 'A') go(`/artefact/${encodeURIComponent(i.name)}${i.status === 'draft' ? `?key=${encodeURIComponent(i.key)}` : ''}`); }}>
                    <td><ArtefactLink name={i.name} keyOf={i.status === 'draft' ? i.key : undefined} /></td>
                    <td>{i.title}</td>
                    <td className="mono">{scopeText(i.scope)}</td>
                    <td>{i.kind}</td>
                    <td>{i.type}</td>
                    <td className="mono">{i.release}</td>
                    <td className="nowrap">
                      <StatusBadge status={i.status} /> {i.tenant !== meta?.owner && <Badge tone="info">{i.tenant}</Badge>} <Provenance status={i.provenance} />
                      {i.generated && <Badge title="Generated by the factory build. Edit the generator, not the file.">generated</Badge>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
