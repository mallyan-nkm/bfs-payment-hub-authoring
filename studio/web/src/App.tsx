import { useEffect, useMemo, useState } from 'react';
import { get, setApiTenant, type Meta } from './api';
import { Studio, ErrorBox } from './ui';
import { Catalogue } from './views/Catalogue';
import { ArtefactPage } from './views/Artefact';
import { IsoData } from './views/IsoData';
import { Designer } from './views/Designer';
import { TestBench } from './views/TestBench';
import { Kit } from './views/Kit';
import { Tenant } from './views/Tenant';
import { Export } from './views/Export';
import { Governance } from './views/Governance';

const NAV: { id: string; label: string; hint: string }[] = [
  { id: 'catalogue', label: 'Catalogue', hint: 'F-01, F-02' },
  { id: 'data', label: 'ISO data', hint: 'F-03, F-04' },
  { id: 'designer', label: 'Workflow designer', hint: 'F-12 to F-15, F-22' },
  { id: 'tests', label: 'Test bench', hint: 'F-16 to F-19' },
  { id: 'kit', label: 'Conformance kit', hint: 'Annex C' },
  { id: 'tenant', label: 'Tenant', hint: 'F-26 to F-29' },
  { id: 'export', label: 'Export', hint: 'F-30, F-31' },
  { id: 'governance', label: 'Governance', hint: 'F-24, F-25, F-32' },
];

function readHash() {
  const h = window.location.hash.replace(/^#\/?/, '');
  const [path, query = ''] = h.split('?');
  return { parts: path.split('/').filter(Boolean).map(decodeURIComponent), query: new URLSearchParams(query) };
}

function loadPref(k: string, d: string) {
  try {
    return localStorage.getItem(k) ?? d;
  } catch {
    return d;
  }
}
function savePref(k: string, v: string) {
  try {
    localStorage.setItem(k, v);
  } catch {
    /* storage not available */
  }
}

export function App() {
  const [route, setRoute] = useState(readHash);
  const [tenant, setTenant] = useState(() => loadPref('th.tenant', 'owner'));
  const [actor, setActor] = useState(() => loadPref('th.actor', 'designer'));
  const [meta, setMeta] = useState<Meta>();
  const [metaError, setMetaError] = useState<string>();
  const [navOpen, setNavOpen] = useState(false);
  setApiTenant(tenant);

  useEffect(() => {
    const on = () => setRoute(readHash());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  useEffect(() => {
    get<Meta>('/api/meta').then(setMeta, (e) => setMetaError(String(e.message ?? e)));
  }, []);
  useEffect(() => savePref('th.tenant', tenant), [tenant]);
  useEffect(() => savePref('th.actor', actor), [actor]);

  const go = (path: string) => {
    window.location.hash = path.startsWith('/') ? path : `/${path}`;
    setNavOpen(false);
  };
  const ctx = useMemo(() => ({ tenant, actor, meta, go }), [tenant, actor, meta]);
  const view = route.parts[0] ?? 'catalogue';

  let body;
  switch (view) {
    case 'artefact': body = <ArtefactPage key={`${tenant}|${route.parts[1]}|${route.query}`} name={route.parts[1]} query={route.query} />; break;
    case 'data': body = <IsoData key={tenant} />; break;
    case 'designer': body = <Designer key={tenant} query={route.query} />; break;
    case 'tests': body = <TestBench key={tenant} query={route.query} />; break;
    case 'kit': body = <Kit />; break;
    case 'tenant': body = <Tenant key={tenant} />; break;
    case 'export': body = <Export key={tenant} query={route.query} />; break;
    case 'governance': body = <Governance key={tenant} />; break;
    default: body = <Catalogue key={tenant} query={route.query} />;
  }

  return (
    <Studio.Provider value={ctx}>
      <div className="shell">
        <header className="topbar">
          <button className="nav-toggle" aria-label="Menu" onClick={() => setNavOpen((o) => !o)}>☰</button>
          <a className="brand" href="#/catalogue">
            <span className="logo" aria-hidden>TH</span>
            <span>TransactHub <b>Designer Studio</b></span>
          </a>
          <div className="topbar-fields">
            <label className="field-inline">
              <span>Tenant</span>
              <select value={tenant} onChange={(e) => setTenant(e.target.value)} aria-label="Tenant">
                {(meta?.tenants ?? [tenant]).map((t) => (
                  <option key={t} value={t}>{t === meta?.owner ? `${t} (shared content)` : t}</option>
                ))}
              </select>
            </label>
            <label className="field-inline">
              <span>Maker</span>
              <input value={actor} onChange={(e) => setActor(e.target.value)} aria-label="Actor name" size={12} />
            </label>
          </div>
        </header>
        {navOpen && <div className="backdrop" onClick={() => setNavOpen(false)} />}
        <nav className={navOpen ? 'sidebar open' : 'sidebar'}>
          {NAV.map((n) => (
            <a key={n.id} href={`#/${n.id}`} className={view === n.id || (view === 'artefact' && n.id === 'catalogue') ? 'nav on' : 'nav'} onClick={() => setNavOpen(false)}>
              <span>{n.label}</span>
              <span className="hint">{n.hint}</span>
            </a>
          ))}
          <div className="sidebar-foot">
            {meta && (
              <p className="muted small">
                {Object.values(meta.counts).reduce((a, b) => a + b, 0)} documents. Design time only: no running payment is read or changed.
              </p>
            )}
          </div>
        </nav>
        <main className="main">
          <ErrorBox error={metaError && `The studio API did not answer: ${metaError}`} />
          {tenant !== (meta?.owner ?? 'owner') && (
            <div className="tenant-strip">Acting for bank tenant <b>{tenant}</b>. Shared content is read-only.</div>
          )}
          {body}
        </main>
      </div>
    </Studio.Provider>
  );
}
