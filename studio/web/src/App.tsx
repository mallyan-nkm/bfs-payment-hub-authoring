import { useCallback, useEffect, useMemo, useState } from 'react';
import { get, me as whoAmI, onUnauthorized, setApiTenant, setCsrf, signOut, type Me, type Meta, type User } from './api';
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
import { Import } from './views/Import';
import { Bank } from './views/Bank';

const NAV: { id: string; label: string; hint: string }[] = [
  { id: 'bank', label: 'Bank workspace', hint: 'F-26 to F-29' },
  { id: 'designer', label: 'Workflow designer', hint: 'F-12 to F-15, F-22, F-33, F-34' },
  { id: 'editors', label: 'Artefact editors', hint: 'F-06 to F-11, F-36' },
  { id: 'catalogue', label: 'Catalogue', hint: 'F-01, F-02' },
  { id: 'data', label: 'ISO data', hint: 'F-03 to F-05' },
  { id: 'assistant', label: 'AI assistant', hint: 'F-20, F-21' },
  { id: 'import', label: 'Import', hint: 'I-33' },
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

/** Sign-in (R-11-32): the studio has no anonymous use. The provider is the one the server is configured with. */
function SignIn({ who }: { who?: Me }) {
  const returnTo = window.location.hash.startsWith('#/') ? window.location.hash : '';
  return (
    <div className="signin">
      <div className="signin-card">
        <h1><span className="logo" aria-hidden>TH</span> TransactHub Designer Studio</h1>
        {who?.signedIn && !who.user ? (
          <>
            <div className="alert fail" role="alert">You are signed in as <b>{who.identity}</b>, who is not a user of the studio. An administrator adds users through a reviewed change to the users and roles.</div>
            <button onClick={() => signOut().then(() => window.location.reload())}>Sign out</button>
          </>
        ) : (
          <>
            <p className="muted">Sign in to design, test and export payment workflows. What you may do follows from your roles.</p>
            <a className="button primary" href={`/auth/login${returnTo ? `?returnTo=${encodeURIComponent(returnTo)}` : ''}`}>Sign in</a>
            {who?.provider === 'development' && <p className="muted small">This studio uses the built-in development sign-in (TH_AUTH_DEV=1). It is for local work and tests only.</p>}
          </>
        )}
      </div>
    </div>
  );
}

export function App() {
  const [route, setRoute] = useState(readHash);
  const [who, setWho] = useState<Me>();
  const [allUsers, setAllUsers] = useState<User[]>([]);
  const [meta, setMeta] = useState<Meta>();
  const [metaError, setMetaError] = useState<string>();
  const [navOpen, setNavOpen] = useState(false);
  const tenant = who?.tenant ?? 'owner';
  const actor = who?.user ?? '';
  setApiTenant(tenant);

  const load = useCallback(() => whoAmI().then((m) => {
    setCsrf(m.csrfToken ?? '');
    setWho(m);
  }), []);
  useEffect(() => {
    load();
    onUnauthorized(() => setWho((w) => (w?.signedIn ? { signedIn: false, provider: w.provider, roles: [], surfaces: [] } : w)));
  }, [load]);
  useEffect(() => {
    const on = () => setRoute(readHash());
    window.addEventListener('hashchange', on);
    on(); // a hash set before this listener was attached
    return () => window.removeEventListener('hashchange', on);
  }, []);
  const signedIn = !!who?.user;
  useEffect(() => {
    if (!signedIn) return;
    get<Meta>('/api/meta').then(setMeta, (e) => setMetaError(String(e.message ?? e)));
    get<User[]>('/api/users').then(setAllUsers, () => setAllUsers([]));
  }, [signedIn]);
  // A role lands on its own surface (R-10-11).
  useEffect(() => {
    if (signedIn && !window.location.hash.replace(/^#\/?/, '') && who?.landing) window.location.hash = `/${who.landing}`;
  }, [signedIn, route.parts, who?.landing]);

  const users = useMemo(() => allUsers.filter((u) => u.tenant === tenant), [allUsers, tenant]);
  const go = (path: string) => {
    window.location.hash = path.startsWith('/') ? path : `/${path}`;
    setNavOpen(false);
  };
  const roles = who?.roles ?? [];
  const surfaces = who?.surfaces ?? [];
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const ctx = useMemo(() => ({ tenant, actor, meta, users, roles, surfaces, setActor: () => {}, go }), [tenant, actor, meta, users, roles.join(), surfaces.join()]);

  if (!who) return <div className="signin"><p className="muted">Loading…</p></div>;
  if (!signedIn) return <SignIn who={who} />;

  const view = route.parts[0] ?? who.landing ?? 'catalogue';
  const shown = view === 'artefact' ? 'catalogue' : view;
  let body;
  if (!surfaces.includes(shown)) {
    body = (
      <div className="view">
        <div className="view-head"><h2>Not for your role</h2></div>
        <div className="alert warn">This part of the studio is not one of your surfaces ({roles.join(', ')}). <a href={`#/${who.landing}`}>Go to your workspace</a>.</div>
      </div>
    );
  } else switch (view) {
    case 'artefact': body = <ArtefactPage key={`${route.parts[1]}|${route.query}`} name={route.parts[1]} query={route.query} />; break;
    case 'bank': body = <Bank />; break;
    case 'data': body = <IsoData query={route.query} />; break;
    case 'designer': body = <Designer query={route.query} />; break;
    case 'tests': body = <TestBench query={route.query} />; break;
    case 'kit': body = <Kit />; break;
    case 'tenant': body = <Tenant />; break;
    case 'export': body = <Export query={route.query} />; break;
    case 'governance': body = <Governance query={route.query} />; break;
    case 'editors': body = <Editors query={route.query} />; break;
    case 'assistant': body = <Assistant />; break;
    case 'import': body = <Import />; break;
    default: body = <Catalogue query={route.query} />;
  }

  return (
    <Studio.Provider value={ctx}>
      <div className="shell">
        <header className="topbar">
          <button className="nav-toggle" aria-label="Menu" onClick={() => setNavOpen((o) => !o)}>☰</button>
          <a className="brand" href={`#/${who.landing ?? 'catalogue'}`}>
            <span className="logo" aria-hidden>TH</span>
            <span>TransactHub <b>Designer Studio</b></span>
          </a>
          <div className="topbar-fields">
            <span className="field-inline" title={`Roles: ${roles.join(', ')}`} aria-label="Signed in as">
              <span>Signed in as</span> <b className="mono">{actor}</b>
              <span className="muted small">{tenant === meta?.owner ? 'shared content' : tenant} · {roles.join(', ') || 'no role'}</span>
            </span>
            <button onClick={() => signOut().then(() => { setWho({ signedIn: false, provider: who.provider, roles: [], surfaces: [] }); window.location.hash = ''; })}>Sign out</button>
          </div>
        </header>
        {navOpen && <div className="backdrop" onClick={() => setNavOpen(false)} />}
        <nav className={navOpen ? 'sidebar open' : 'sidebar'}>
          {NAV.filter((n) => surfaces.includes(n.id)).map((n) => (
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
            <div className="tenant-strip">Working for bank tenant <b>{tenant}</b>, in scope b only. Shared content is read-only.</div>
          )}
          {body}
        </main>
        {surfaces.includes('assistant') && <AssistantDock />}
      </div>
    </Studio.Provider>
  );
}
