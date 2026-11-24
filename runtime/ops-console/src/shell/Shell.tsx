import { useContext, useEffect, useState, type ReactElement, type ReactNode } from 'react';
import { Avatar, Button, Card, Nav, NavItem, SearchBox, Tooltip } from '@fluentui/react-components';
import {
  AlertRegular, ArrowUploadRegular, BeakerRegular, BookDatabaseRegular, BuildingBankRegular, ChartMultipleRegular, ClipboardTaskListLtrRegular,
  DocumentBulletListClockRegular, HistoryRegular, LockClosedRegular, NavigationRegular, PaymentRegular, PeopleTeamRegular, ServerRegular,
  ShieldErrorRegular, ShieldTaskRegular, SignOutRegular, TimerRegular, WarningRegular,
} from '@fluentui/react-icons';
import type { DevIdentity, SignIn } from './auth';
import { Alert, ThemeChoiceContext, ThemeMenu, initials } from '../ui';

// The shell the runtime's web apps share: the sign-in, the top bar and the sections in the side bar. The operations
// console registers its sections (payments, approvals), the service console its Service section, the test bench its Test bench.
// Drawn with Fluent UI 2 (docs/adr/0003-ui-fluent.md): a navy top bar, a navigation with icons, the page beside it.

export interface Section {
  id: string;
  title: string;
  /** Matches a hash route: #/<id>/... */
  render(path: string[], ctx: ShellContext): ReactNode;
}

export interface ShellContext {
  auth: SignIn;
  go(hash: string): void;
  /** Shown in the top bar, right: the projection's lag, for example. */
  status?: ReactNode;
}

/** An icon for each section the consoles register; a section with none gets a generic one. */
const ICONS: Record<string, ReactElement> = {
  payments: <PaymentRegular />,
  work: <ClipboardTaskListLtrRegular />,
  approvals: <ShieldTaskRegular />,
  alerts: <ShieldErrorRegular />,
  ledger: <BookDatabaseRegular />,
  reports: <ChartMultipleRegular />,
  breaks: <WarningRegular />,
  turnaround: <TimerRegular />,
  retention: <DocumentBulletListClockRegular />,
  consents: <LockClosedRegular />,
  upload: <ArrowUploadRegular />,
  audit: <HistoryRegular />,
  registers: <PeopleTeamRegular />,
  service: <ServerRegular />,
  testbench: <BeakerRegular />,
};

function useHash(): string[] {
  const read = () => location.hash.replace(/^#\/?/, '').split('?')[0].split('/').filter(Boolean).map(decodeURIComponent);
  const [path, setPath] = useState(read);
  useEffect(() => {
    const on = () => setPath(read());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return path;
}

/** Synthetic users for the dev sign-in (TH_AUTH_DEV=1 only): never real names. */
export const DEV_USERS: DevIdentity[] = [
  { sub: 'ops.operator', name: 'Example operator', roles: ['example-bank:Operator'] },
  { sub: 'ops.supervisor', name: 'Example supervisor', roles: ['example-bank:Supervisor'] },
  { sub: 'ops.other', name: 'Other bank operator', roles: ['other-bank:Operator'] },
  { sub: 'reg.maker', name: 'Example register maker', roles: ['example-bank:RegisterMaker'] },
  { sub: 'reg.approver', name: 'Example bank approver', roles: ['example-bank:BankApprover'] },
  { sub: 'svc.engineer', name: 'Example service engineer', roles: ['*:ServiceEngineer'] },
  { sub: 'tb.tester', name: 'Example tester', roles: ['*:Tester'] },
  { sub: 'tb.reader', name: 'Example reader without a role', roles: [] },
];

function SignInPage({ auth, done }: { auth: SignIn; done(): void }) {
  const [error, setError] = useState<string | null>(null);
  return (
    <main className="signin">
      <Card className="signin-card" appearance="outline">
        <h1><span className="logo" aria-hidden>TH</span> TransactHub Operations</h1>
        {auth.config.mode === 'dev' ? (
          <>
            <p className="muted">Dev sign-in: synthetic users only. The API refuses this mode in production.</p>
            <div className="signin-users">
              {DEV_USERS.map((u) => (
                <Button key={u.sub} className="signin-user" onClick={async () => { await auth.signIn(u); done(); }}>
                  Sign in as {u.name} <span className="muted">({u.roles.join(', ')})</span>
                </Button>
              ))}
            </div>
          </>
        ) : (
          <>
            <p className="muted">Sign in with your bank's identity provider.</p>
            <Button appearance="primary" onClick={() => auth.signIn().catch((e) => setError(String(e.message ?? e)))}>Sign in</Button>
          </>
        )}
        {error && <Alert tone="fail" className="error">{error}</Alert>}
      </Card>
    </main>
  );
}

export function Shell({ auth, sections, title, status }: { auth: SignIn; sections: Section[]; title: string; status?: ReactNode }) {
  const path = useHash();
  const [, setTick] = useState(0);
  const [navOpen, setNavOpen] = useState(false);
  const [find, setFind] = useState('');
  const [searched, setSearched] = useState(0);
  const theme = useContext(ThemeChoiceContext);
  const who = auth.current();
  const changed = () => { setTick((t) => t + 1); window.dispatchEvent(new Event('th-auth-changed')); };
  if (!who) return <SignInPage auth={auth} done={changed} />;
  const section = sections.find((s) => s.id === path[0]) ?? sections[0];
  const ctx: ShellContext = { auth, go: (h) => { location.hash = h; }, status };
  const product = title.replace(/^TransactHub\s*/, '');
  const payments = sections.some((s) => s.id === 'payments');
  const work = sections.find((s) => s.id === 'work' || s.id === 'approvals');
  return (
    <div className="app">
      <header className="top">
        <Button className="nav-toggle" appearance="subtle" icon={<NavigationRegular />} aria-label="Menu" aria-expanded={navOpen} onClick={() => setNavOpen((o) => !o)} />
        <span className="brand"><span className="logo" aria-hidden>TH</span><span>TransactHub <b>{product}</b></span></span>
        {payments && (
          // The global search: a payment by its id, reference or file name, on the payments list.
          <form className="top-search" role="search" aria-label="Find a payment" onSubmit={(e) => { e.preventDefault(); location.hash = `#/payments?${new URLSearchParams(find.trim() ? { q: find.trim() } : {})}`; setSearched((n) => n + 1); }}>
            <SearchBox size="small" value={find} onChange={(_, d) => setFind(d.value)} placeholder="Find a payment by id, reference or file" aria-label="Find a payment" />
          </form>
        )}
        <span className="top-status">{status}</span>
        {work && (
          <Tooltip content={`Open ${work.title.toLowerCase()}`} relationship="label">
            <Button as="a" href={`#/${work.id}`} appearance="subtle" className="topbar-button" icon={<AlertRegular />} />
          </Tooltip>
        )}
        {theme && <ThemeMenu choice={theme.choice} onChange={theme.setChoice} />}
        <span className="who-box">
          <Avatar size={28} initials={initials(who.name)} color="colorful" idForColor={who.subject} aria-hidden />
          <span className="who" data-testid="who">{who.name}</span>
        </span>
        <Button appearance="subtle" className="topbar-button" icon={<SignOutRegular />} onClick={() => { auth.signOut(); changed(); }}>Sign out</Button>
      </header>
      {navOpen && <div className="backdrop" onClick={() => setNavOpen(false)} />}
      <nav className={navOpen ? 'side open' : 'side'} aria-label="Sections">
        <Nav selectedValue={section?.id} className="side-nav">
          {sections.map((s) => (
            <NavItem key={s.id} value={s.id} href={`#/${s.id}`} icon={ICONS[s.id] ?? <BuildingBankRegular />} className={s === section ? 'active' : ''} onClick={() => setNavOpen(false)}>
              {s.title}
            </NavItem>
          ))}
        </Nav>
      </nav>
      <main className="content" key={searched}>{section.render(path.slice(1), ctx)}</main>
    </div>
  );
}
