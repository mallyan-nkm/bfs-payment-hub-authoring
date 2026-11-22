import { useEffect, useState, type ReactNode } from 'react';
import type { DevIdentity, SignIn } from './auth';

// The shell the runtime's web apps share: the sign-in, the top bar and the sections in the side bar. The operations
// console registers its sections (payments, approvals); the test bench can register its own beside them.

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
];

function SignInPage({ auth, done }: { auth: SignIn; done(): void }) {
  const [error, setError] = useState<string | null>(null);
  return (
    <main className="signin">
      <h1>TransactHub Operations</h1>
      {auth.config.mode === 'dev' ? (
        <>
          <p className="muted">Dev sign-in: synthetic users only. The API refuses this mode in production.</p>
          <div className="signin-users">
            {DEV_USERS.map((u) => (
              <button key={u.sub} onClick={async () => { await auth.signIn(u); done(); }}>
                Sign in as {u.name} <span className="muted">({u.roles.join(', ')})</span>
              </button>
            ))}
          </div>
        </>
      ) : (
        <>
          <p className="muted">Sign in with your bank's identity provider.</p>
          <button className="primary" onClick={() => auth.signIn().catch((e) => setError(String(e.message ?? e)))}>Sign in</button>
        </>
      )}
      {error && <p className="error">{error}</p>}
    </main>
  );
}

export function Shell({ auth, sections, title, status }: { auth: SignIn; sections: Section[]; title: string; status?: ReactNode }) {
  const path = useHash();
  const [, setTick] = useState(0);
  const who = auth.current();
  const changed = () => { setTick((t) => t + 1); window.dispatchEvent(new Event('th-auth-changed')); };
  if (!who) return <SignInPage auth={auth} done={changed} />;
  const section = sections.find((s) => s.id === path[0]) ?? sections[0];
  const ctx: ShellContext = { auth, go: (h) => { location.hash = h; }, status };
  return (
    <div className="app">
      <header className="top">
        <span className="brand">{title}</span>
        <span className="top-status">{status}</span>
        <span className="who" data-testid="who">{who.name}</span>
        <button className="link" onClick={() => { auth.signOut(); changed(); }}>Sign out</button>
      </header>
      <nav className="side" aria-label="Sections">
        {sections.map((s) => (
          <a key={s.id} href={`#/${s.id}`} className={s === section ? 'active' : ''}>{s.title}</a>
        ))}
      </nav>
      <main className="content">{section.render(path.slice(1), ctx)}</main>
    </div>
  );
}
