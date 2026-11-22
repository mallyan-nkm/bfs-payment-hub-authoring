import { StrictMode, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { loadSignIn, type SignIn } from './shell/auth';
import { Shell, type Section } from './shell/Shell';
import { client, type Client, type Lag, type View } from './ops/api';
import { Payments } from './ops/Payments';
import { Detail } from './ops/Detail';
import { show } from './ops/format';
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

function App({ auth }: { auth: SignIn }) {
  const api = client(auth);
  const [views, setViews] = useState<View[] | null>(null);
  const signedIn = !!auth.current();
  useEffect(() => { if (signedIn) api.views().then(setViews).catch(() => setViews([])); }, [signedIn]);
  const sections: Section[] = [
    {
      id: 'payments', title: 'Payments',
      render: (path, ctx) => !views ? <p className="muted">Loading…</p> : path[0] ? <Detail api={api} views={views} id={path[0]} go={ctx.go} /> : <Payments api={api} views={views} go={ctx.go} />,
    },
    { id: 'approvals', title: 'Approvals', render: (_p, ctx) => <Approvals api={api} go={ctx.go} /> },
  ];
  return <Shell auth={auth} sections={sections} title="TransactHub Operations" status={signedIn ? <LagBadge api={api} /> : null} />;
}

loadSignIn().then((auth) => {
  // Re-render on sign-in and sign-out (the shell asks the sign-in who is current).
  const root = createRoot(document.getElementById('root')!);
  const render = () => root.render(<StrictMode><App key={auth.current()?.subject ?? 'none'} auth={auth} /></StrictMode>);
  render();
  window.addEventListener('th-auth-changed', render);
});
