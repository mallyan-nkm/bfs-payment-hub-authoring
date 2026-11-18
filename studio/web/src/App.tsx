import { useEffect, useMemo, useState } from 'react';
import { get, isChecker, isDesigner, setApiTenant, type Meta, type User } from './api';
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
import { Editors } from './views/Editors';
import { Assistant } from './views/Assistant';
import { AssistantDock } from './views/AssistantDock';

const NAV: { id: string; label: string; hint: string }[] = [
  { id: 'catalogue', label: 'Catalogue', hint: 'F-01, F-02' },
  { id: 'data', label: 'ISO data', hint: 'F-03 to F-05' },
  { id: 'designer', label: 'Workflow designer', hint: 'F-12 to F-15, F-22, F-33, F-34' },
  { id: 'editors', label: 'Artefact editors', hint: 'F-06 to F-11, F-36' },
  { id: 'assistant', label: 'AI assistant', hint: 'F-20, F-21' },
  { id: 'tests', label: 'Test bench', hint: 'F-16 to F-19, F-35' },
  { id: 'kit', label: 'Conformance kit', hint: 'Annex C' },
  { id: 'tenant', label: 'Tenant', hint: 'F-26 to F-29' },
  { id: 'export', label: 'Export', hint: 'F-30, F-31' },
  { id: 'governance', label: 'Governance', hint: 'F-23 to F-25, F-32' },
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
  const [actor, setActorState] = useState(() => loadPref(`th.actor.${loadPref('th.tenant', 'owner')}`, ''));
  const [allUsers, setAllUsers] = useState<User[]>([]);
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
  useEffect(() => {
    get<User[]>('/api/users', 'owner').then(setAllUsers, () => setAllUsers([]));
  }, []);
  useEffect(() => savePref('th.tenant', tenant), [tenant]);
  // The users of this tenant. The actor is one of them; the default is its designer (C17).
  const users = useMemo(() => allUsers.filter((u) => u.tenant === tenant), [allUsers, tenant]);
  useEffect(() => {
    if (!users.length) return;
    if (!users.some((u) => u.id === actor)) {
      const remembered = loadPref(`th.actor.${tenant}`, '');
      setActorState(users.some((u) => u.id === remembered) ? remembered : (users.find(isDesigner) ?? users[0]).id);
    }
  }, [users, actor, tenant]);
  const setActor = (id: string) => {
    setActorState(id);
    savePref(`th.actor.${tenant}`, id);
  };
  const actorUser = users.find((u) => u.id === actor);

  const go = (path: string) => {
    window.location.hash = path.startsWith('/') ? path : `/${path}`;
    setNavOpen(false);
  };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const ctx = useMemo(() => ({ tenant, actor, meta, users, setActor, go }), [tenant, actor, meta, users]);
  const view = route.parts[0] ?? 'catalogue';

  let body;
  switch (view) {
    case 'artefact': body = <ArtefactPage key={`${tenant}|${route.parts[1]}|${route.query}`} name={route.parts[1]} query={route.query} />; break;
    case 'data': body = <IsoData key={tenant} query={route.query} />; break;
    case 'designer': body = <Designer key={tenant} query={route.query} />; break;
    case 'tests': body = <TestBench key={tenant} query={route.query} />; break;
    case 'kit': body = <Kit />; break;
    case 'tenant': body = <Tenant key={tenant} />; break;
    case 'export': body = <Export key={tenant} query={route.query} />; break;
    case 'governance': body = <Governance key={tenant} query={route.query} />; break;
    case 'editors': body = <Editors key={tenant} query={route.query} />; break;
    case 'assistant': body = <Assistant key={tenant} />; break;
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
            <label className="field-inline" title={actorUser ? `Roles: ${actorUser.roles.join(', ')}` : undefined}>
              <span>Acting as</span>
              <select value={actor} onChange={(e) => setActor(e.target.value)} aria-label="Acting as">
                {!users.length && <option value={actor}>{actor || 'loading users'}</option>}
                {users.map((u) => (
                  <option key={u.id} value={u.id}>{u.id} ({u.roles.join(', ')})</option>
                ))}
              </select>
            </label>
            {actorUser && !isDesigner(actorUser) && !isChecker(actorUser) && <span className="badge warn" title="This user may not author or approve">read and review only</span>}
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
        <AssistantDock />
      </div>
    </Studio.Provider>
  );
}
