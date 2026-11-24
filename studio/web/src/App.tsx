import { useCallback, useEffect, useMemo, useState, type ReactElement } from 'react';
import { Avatar, Badge as FluentBadge, Button, Card, Nav, NavItem, SearchBox, Spinner, Tooltip } from '@fluentui/react-components';
import {
  ArrowExportRegular, ArrowImportRegular, BeakerRegular, BuildingRegular, DatabaseRegular, DocumentEditRegular, FlowchartRegular,
  GavelRegular, LibraryRegular, NavigationRegular, PeopleTeamRegular, ShieldCheckmarkRegular, SignOutRegular, SparkleRegular,
} from '@fluentui/react-icons';
import { get, heartbeat, me as whoAmI, onUnauthorized, setApiTenant, setCsrf, signOut, type Me, type Meta, type User } from './api';
import { Alert, Studio, ErrorBox, ThemeMenu, initials } from './ui';
import type { ThemeChoice } from './theme';
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

const NAV: { id: string; label: string; hint: string; icon: ReactElement }[] = [
  { id: 'bank', label: 'Bank workspace', hint: 'F-26 to F-29', icon: <BuildingRegular /> },
  { id: 'designer', label: 'Workflow designer', hint: 'F-12 to F-15, F-22, F-33, F-34', icon: <FlowchartRegular /> },
  { id: 'editors', label: 'Artefact editors', hint: 'F-06 to F-11, F-36', icon: <DocumentEditRegular /> },
  { id: 'catalogue', label: 'Catalogue', hint: 'F-01, F-02', icon: <LibraryRegular /> },
  { id: 'data', label: 'ISO data', hint: 'F-03 to F-05', icon: <DatabaseRegular /> },
  { id: 'assistant', label: 'AI assistant', hint: 'F-20, F-21', icon: <SparkleRegular /> },
  { id: 'import', label: 'Import', hint: 'I-33', icon: <ArrowImportRegular /> },
  { id: 'tests', label: 'Test bench', hint: 'F-16 to F-19, F-35', icon: <BeakerRegular /> },
  { id: 'kit', label: 'Conformance kit', hint: 'Annex C', icon: <ShieldCheckmarkRegular /> },
  { id: 'tenant', label: 'Tenant', hint: 'F-26 to F-29', icon: <PeopleTeamRegular /> },
  { id: 'export', label: 'Export', hint: 'F-30, F-31', icon: <ArrowExportRegular /> },
  { id: 'governance', label: 'Governance', hint: 'F-23 to F-25, F-32', icon: <GavelRegular /> },
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
      <Card className="signin-card" appearance="outline">
        <h1><span className="logo" aria-hidden>TH</span> TransactHub Designer Studio</h1>
        {who?.signedIn && !who.user ? (
          <>
            <Alert tone="fail" role="alert">You are signed in as <b>{who.identity}</b>, who is not a user of the studio. An administrator adds users through a reviewed change to the users and roles.</Alert>
            <Button icon={<SignOutRegular />} onClick={() => signOut().then(() => window.location.reload())}>Sign out</Button>
          </>
        ) : (
          <>
            <p className="muted">Sign in to design, test and export payment workflows. What you may do follows from your roles.</p>
            <Button as="a" appearance="primary" href={`/auth/login${returnTo ? `?returnTo=${encodeURIComponent(returnTo)}` : ''}`}>Sign in</Button>
            {who?.provider === 'development' && <p className="muted small">This studio uses the built-in development sign-in (TH_AUTH_DEV=1). It is for local work and tests only.</p>}
          </>
        )}
      </Card>
    </div>
  );
}

export function App({ themeChoice, setThemeChoice }: { themeChoice: ThemeChoice; setThemeChoice: (c: ThemeChoice) => void }) {
  const [route, setRoute] = useState(readHash);
  const [who, setWho] = useState<Me>();
  const [allUsers, setAllUsers] = useState<User[]>([]);
  const [meta, setMeta] = useState<Meta>();
  const [metaError, setMetaError] = useState<string>();
  const [navOpen, setNavOpen] = useState(false);
  const [find, setFind] = useState('');
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
  // The heartbeat (R-11-50): every few minutes the studio checks the session with the identity provider. The page asks for it
  // without counting as use, so the idle time still runs; a session the provider ended shows the sign-in page.
  useEffect(() => {
    if (!signedIn) return;
    const every = Math.max(1, Number(who?.heartbeatSeconds ?? 300)) * 1000;
    const t = window.setInterval(() => {
      heartbeat().then((alive) => { if (!alive) setWho((w) => (w?.signedIn ? { signedIn: false, provider: w.provider, roles: [], surfaces: [] } : w)); }, () => undefined);
    }, every);
    return () => window.clearInterval(t);
  }, [signedIn, who]);
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

  if (!who) return <div className="signin"><Spinner size="small" label="Loading…" /></div>;
  if (!signedIn) return <SignIn who={who} />;

  const view = route.parts[0] ?? who.landing ?? 'catalogue';
  const shown = view === 'artefact' ? 'catalogue' : view;
  let body;
  if (!surfaces.includes(shown)) {
    body = (
      <div className="view">
        <div className="view-head"><h2>Not for your role</h2></div>
        <Alert tone="warn">This part of the studio is not one of your surfaces ({roles.join(', ')}). <a href={`#/${who.landing}`}>Go to your workspace</a>.</Alert>
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
    // Keyed by the search, so the top bar's search opens the catalogue on what was asked.
    default: body = <Catalogue key={route.query.get('q') ?? ''} query={route.query} />;
  }

  const shared = tenant === (meta?.owner ?? 'owner');
  return (
    <Studio.Provider value={ctx}>
      <div className="shell">
        <header className="topbar">
          <Button className="nav-toggle" appearance="subtle" icon={<NavigationRegular />} aria-label="Menu" aria-expanded={navOpen} onClick={() => setNavOpen((o) => !o)} />
          <a className="brand" href={`#/${who.landing ?? 'catalogue'}`}>
            <span className="logo" aria-hidden>TH</span>
            <span>TransactHub <b>Designer Studio</b></span>
          </a>
          {surfaces.includes('catalogue') && (
            <form className="topbar-search" role="search" aria-label="Find" onSubmit={(e) => { e.preventDefault(); go(`/catalogue?q=${encodeURIComponent(find)}`); }}>
              <SearchBox size="small" value={find} onChange={(_, d) => setFind(d.value)} placeholder="Find an artefact by name or path" aria-label="Find in the catalogue" />
            </form>
          )}
          <div className="topbar-fields">
            <Tooltip content={shared ? 'Shared content: the platform and the schemes' : 'A bank tenant: scope b only; shared content is read-only'} relationship="description">
              <FluentBadge appearance="filled" color={shared ? 'informative' : 'warning'} className="topbar-badge">{shared ? 'shared content' : tenant}</FluentBadge>
            </Tooltip>
            <Tooltip content="Design time only: no running payment is read or changed." relationship="description">
              <FluentBadge appearance="outline" className="topbar-badge topbar-design">design time</FluentBadge>
            </Tooltip>
            <ThemeMenu choice={themeChoice} onChange={setThemeChoice} />
            <span className="who" title={`Roles: ${roles.join(', ')}`} aria-label="Signed in as">
              <Avatar size={28} initials={initials(actor)} color="colorful" idForColor={actor} aria-hidden />
              <span className="who-text">
                <b className="mono">{actor}</b>
                <span className="small who-roles">{shared ? 'shared content' : tenant} · {roles.join(', ') || 'no role'}</span>
              </span>
            </span>
            <Button appearance="subtle" className="topbar-button" icon={<SignOutRegular />} onClick={() => signOut().then(() => { setWho({ signedIn: false, provider: who.provider, roles: [], surfaces: [] }); window.location.hash = ''; })}>Sign out</Button>
          </div>
        </header>
        {navOpen && <div className="backdrop" onClick={() => setNavOpen(false)} />}
        <nav className={navOpen ? 'sidebar open' : 'sidebar'} aria-label="Studio">
          <Nav selectedValue={shown} className="side-nav">
            {NAV.filter((n) => surfaces.includes(n.id)).map((n) => (
              <NavItem key={n.id} value={n.id} href={`#/${n.id}`} icon={n.icon} className="nav" data-testid={`nav-${n.id}`} onClick={() => setNavOpen(false)}>
                <span className="nav-text">
                  <span data-testid="nav-label">{n.label}</span>
                  <span className="hint">{n.hint}</span>
                </span>
              </NavItem>
            ))}
          </Nav>
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
          {!shared && (
            <Alert tone="warn" className="tenant-strip">Working for bank tenant <b>{tenant}</b>, in scope b only. Shared content is read-only.</Alert>
          )}
          {body}
        </main>
        {surfaces.includes('assistant') && <AssistantDock />}
      </div>
    </Studio.Provider>
  );
}
