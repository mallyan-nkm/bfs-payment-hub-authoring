import { StrictMode, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { loadSignIn, type SignIn } from './shell/auth';
import { Shell, type Section } from './shell/Shell';
import { client, type Client, type Lag, type View } from './ops/api';
import { Payments } from './ops/Payments';
import { Detail } from './ops/Detail';
import { Reports } from './ops/Reports';
import { show } from './ops/format';
import { serviceClient } from './service/api';
import { ServiceSection } from './service/Service';
import { benchClient } from './testbench/api';
import { TestBenchSection } from './testbench/TestBench';
import './styles.css';

function LagBadge({ api }: { api: Client }) {
  const [lag, setLag] = useState<Lag | null>(null);
  useEffect(() => {
    const load = () => api.health().then((h) => setLag(h.lag)).catch(() => setLag(null));
    load();
    const t = setInterval(load, 2000);
    return () => clearInterval(t);
  }, []);
  if (!lag) return null;
  const ok = lag.behind === 0;
  return (
    <span className={`badge ${ok ? 'pass' : lag.oldestSeconds > 30 ? 'fail' : 'warn'}`} data-testid="lag" title={`Applied ${lag.applied} facts; last fact ${show(lag.lastFactAt, 'datetime')}`}>
      Read model {ok ? 'up to date' : `${lag.behind} behind, ${Math.round(lag.oldestSeconds)} s`}
    </span>
  );
}

function Approvals({ api, go }: { api: Client; go(h: string): void }) {
  const [rows, setRows] = useState<any[] | null>(null);
  useEffect(() => { api.pending().then(setRows).catch(() => setRows([])); }, []);
  return (
    <section>
      <h1>Waiting for a second approver</h1>
      {rows?.length === 0 && <p className="muted">Nothing waits for you.</p>}
      <table className="grid" aria-label="Pending approvals">
        <thead><tr><th>Action</th><th>Payment</th><th>Asked by</th><th>Remarks</th><th>Needs</th></tr></thead>
        <tbody>{rows?.map((a) => (
          <tr key={a.id} className="clickable" onClick={() => go(`#/payments/${encodeURIComponent(a.subjectId)}`)}>
            <td>{a.action}</td><td className="mono">{a.subjectId}</td><td>{a.requestedBy}</td><td>{a.remarks ?? '—'}</td><td>{a.secondApprover}</td>
          </tr>
        ))}</tbody>
      </table>
    </section>
  );
}

/** Which of the runtime's APIs answer here: the operations console's, the service console's, the test bench's, or several behind one gateway. */
interface Hosts { ops: boolean; service: boolean; testbench: boolean }

function ServiceGate({ auth, path }: { auth: SignIn; path: string[] }) {
  const api = serviceClient(auth);
  const [me, setMe] = useState<{ roles: string[]; service: boolean } | null | undefined>(undefined);
  useEffect(() => { api.me().then(setMe).catch(() => setMe(null)); }, []);
  if (me === undefined) return <p className="muted">Loading…</p>;
  if (!me?.service) return <p className="muted">You have no service role (ServiceEngineer or ServiceApprover) in the bank's role map.</p>;
  return <ServiceSection api={api} path={path} approver={me.roles.includes('ServiceApprover')} />;
}

/** The test bench needs the Tester role (or a role the installation lets in); the API says which the caller has. */
function TestBenchGate({ auth, path, go }: { auth: SignIn; path: string[]; go(h: string): void }) {
  const api = benchClient(auth);
  const [me, setMe] = useState<{ roles: string[] } | string | undefined>(undefined);
  useEffect(() => { api.me().then(setMe).catch((e) => setMe(String(e.message ?? e))); }, []);
  if (me === undefined) return <p className="muted">Loading…</p>;
  if (typeof me === 'string') return <p className="error" data-testid="testbench-refused">{me}</p>;
  return <TestBenchSection api={api} path={path} go={go} />;
}

function App({ auth, hosts }: { auth: SignIn; hosts: Hosts }) {
  const api = client(auth);
  const [views, setViews] = useState<View[] | null>(null);
  const signedIn = !!auth.current();
  useEffect(() => { if (signedIn && hosts.ops) api.views().then(setViews).catch(() => setViews([])); }, [signedIn]);
  const sections: Section[] = [];
  if (hosts.ops) sections.push(
    {
      id: 'payments', title: 'Payments',
      render: (path, ctx) => !views ? <p className="muted">Loading…</p> : path[0] ? <Detail api={api} views={views} id={path[0]} go={ctx.go} /> : <Payments api={api} views={views} go={ctx.go} />,
    },
    { id: 'approvals', title: 'Approvals', render: (_p, ctx) => <Approvals api={api} go={ctx.go} /> },
    { id: 'reports', title: 'Reports', render: () => <Reports api={api} /> },
  );
  if (hosts.service) sections.push({ id: 'service', title: 'Service', render: (path) => <ServiceGate auth={auth} path={path} /> });
  if (hosts.testbench) sections.push({ id: 'testbench', title: 'Test bench', render: (path, ctx) => <TestBenchGate auth={auth} path={path} go={ctx.go} /> });
  return <Shell auth={auth} sections={sections} title={hosts.ops ? 'TransactHub Operations' : hosts.service ? 'TransactHub Service' : 'TransactHub Test bench'} status={signedIn && hosts.ops ? <LagBadge api={api} /> : null} />;
}

async function answers(url: string): Promise<boolean> {
  try { return (await fetch(url)).ok; } catch { return false; }
}

(async () => {
  const hosts: Hosts = { ops: await answers('/ops/api/auth/config'), service: await answers('/service/api/auth/config'), testbench: await answers('/testbench/api/auth/config') };
  // One sign-in for every section: each API gives the same client (transacthub-runtime-ui) and issuer; the first that answers says how.
  const auth = await loadSignIn(hosts.ops ? '/ops/api/auth/config' : hosts.service ? '/service/api/auth/config' : hosts.testbench ? '/testbench/api/auth/config' : '/ops/api/auth/config');
  // Re-render on sign-in and sign-out (the shell asks the sign-in who is current).
  const root = createRoot(document.getElementById('root')!);
  const render = () => root.render(<StrictMode><App key={auth.current()?.subject ?? 'none'} auth={auth} hosts={hosts} /></StrictMode>);
  render();
  window.addEventListener('th-auth-changed', render);
})();
