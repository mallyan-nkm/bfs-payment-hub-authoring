import { StrictMode, useEffect, useState, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { loadSignIn, type SignIn } from './shell/auth';
import { Shell, type Section } from './shell/Shell';
import { client, type Client, type Lag, type View } from './ops/api';
import { Payments } from './ops/Payments';
import { Detail } from './ops/Detail';
import { Reports } from './ops/Reports';
import { Breaks } from './ops/Breaks';
import { Work } from './ops/Work';
import { Upload } from './ops/Upload';
import { Alerts, Ledger, RatesBadge } from './ops/Oversight';
import { AuditTrail } from './shell/AuditTrail';
import { Consents, Retention, Turnaround } from './ops/Controls';
import { show } from './ops/format';
import { serviceClient } from './service/api';
import { ServiceSection } from './service/Service';
import { benchClient } from './testbench/api';
import { TestBenchSection } from './testbench/TestBench';
import { registersClient } from './registers/api';
import { RegistersSection } from './registers/Registers';
import './styles.css';
import { createDOMRenderer, FluentProvider, RendererProvider, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow } from '@fluentui/react-components';
import { Alert, Badge, ThemeChoiceContext } from './ui';
import { useThemeChoice } from './theme';

// Fluent UI 2 with the TransactHub brand theme, light or dark (docs/adr/0003-ui-fluent.md). Fluent writes its styles into
// style elements; the page's content security policy allows only those that carry the nonce the host put in this page.
const nonce = document.querySelector('meta[name="th-style-nonce"]')?.getAttribute('content') || undefined;
const renderer = createDOMRenderer(document, nonce ? { styleElementAttributes: { nonce } } : {});

function Themed({ children }: { children: ReactNode }) {
  const { choice, mode, theme, setChoice } = useThemeChoice();
  return (
    <RendererProvider renderer={renderer} targetDocument={document}>
      <FluentProvider theme={theme} className={`th-${mode}`}>
        <ThemeChoiceContext.Provider value={{ choice, setChoice }}>
          <div className="th-root">{children}</div>
        </ThemeChoiceContext.Provider>
      </FluentProvider>
    </RendererProvider>
  );
}

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
    <Badge className={`badge ${ok ? 'pass' : lag.oldestSeconds > 30 ? 'fail' : 'warn'}`} data-testid="lag" title={`Applied ${lag.applied} facts; last fact ${show(lag.lastFactAt, 'datetime')}`}>
      Read model {ok ? 'up to date' : `${lag.behind} behind, ${Math.round(lag.oldestSeconds)} s`}
    </Badge>
  );
}

function Approvals({ api, go }: { api: Client; go(h: string): void }) {
  const [rows, setRows] = useState<any[] | null>(null);
  useEffect(() => { api.pending().then(setRows).catch(() => setRows([])); }, []);
  return (
    <section>
      <h1>Waiting for a second approver</h1>
      {rows?.length === 0 && <p className="muted">Nothing waits for you.</p>}
      <Table size="small" className="grid" aria-label="Pending approvals">
        <TableHeader><TableRow><TableHeaderCell>Action</TableHeaderCell><TableHeaderCell>Payment</TableHeaderCell><TableHeaderCell>Asked by</TableHeaderCell><TableHeaderCell>Remarks</TableHeaderCell><TableHeaderCell>Needs</TableHeaderCell></TableRow></TableHeader>
        <TableBody>{rows?.map((a) => (
          <TableRow key={a.id} className="clickable" onClick={() => go(`#/payments/${encodeURIComponent(a.subjectId)}`)}>
            <TableCell>{a.action}</TableCell><TableCell className="mono">{a.subjectId}</TableCell><TableCell>{a.requestedBy}</TableCell><TableCell>{a.remarks ?? '—'}</TableCell><TableCell>{a.secondApprover}</TableCell>
          </TableRow>
        ))}</TableBody>
      </Table>
    </section>
  );
}

/** Which of the runtime's APIs answer here: the operations console's, the service console's, the test bench's, or several behind one gateway. */
interface Hosts { ops: boolean; service: boolean; testbench: boolean }

function ServiceGate({ auth, path }: { auth: SignIn; path: string[] }) {
  const api = serviceClient(auth);
  const [me, setMe] = useState<{ roles: string[]; service: boolean } | null | undefined>(undefined);
  useEffect(() => { api.me().then(setMe).catch(() => setMe(null)); }, []);
  if (me === undefined) return <Spinner size="tiny" labelPosition="after" label="Loading…" className="loading" />;
  if (!me?.service) return <p className="muted">You have no service role (ServiceEngineer or BankApprover) in the bank's role map.</p>;
  return <ServiceSection api={api} path={path} approver={me.roles.includes('BankApprover')} />;
}

/** The test bench needs the Tester role (or a role the installation lets in); the API says which the caller has. */
function TestBenchGate({ auth, path, go }: { auth: SignIn; path: string[]; go(h: string): void }) {
  const api = benchClient(auth);
  const [me, setMe] = useState<{ roles: string[] } | string | undefined>(undefined);
  useEffect(() => { api.me().then(setMe).catch((e) => setMe(String(e.message ?? e))); }, []);
  if (me === undefined) return <Spinner size="tiny" labelPosition="after" label="Loading…" className="loading" />;
  if (typeof me === 'string') return <Alert tone="fail" className="error" data-testid="testbench-refused">{me}</Alert>;
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
      render: (path, ctx) => !views ? <Spinner size="tiny" labelPosition="after" label="Loading…" className="loading" /> : path[0] ? <Detail api={api} views={views} id={path[0]} go={ctx.go} /> : <Payments api={api} views={views} go={ctx.go} />,
    },
    // Work management (R-05-71): the queue, taken, escalated and given out, live.
    { id: 'work', title: 'Work queue', render: (_p, ctx) => <Work api={api} go={ctx.go} /> },
    { id: 'approvals', title: 'Approvals', render: (_p, ctx) => <Approvals api={api} go={ctx.go} /> },
    // The fraud alerts (R-05-90) and, for supervisors, the bank's ledger accounts, read-only (R-05-91).
    { id: 'alerts', title: 'Fraud alerts', render: (_p, ctx) => <Alerts api={api} go={ctx.go} /> },
    { id: 'ledger', title: 'Ledger accounts', render: () => <Ledger api={api} /> },
    { id: 'reports', title: 'Reports', render: () => <Reports api={api} /> },
    // Reconciliation breaks (R-09-92): a bank approver consents to a resolution that changes the system.
    { id: 'breaks', title: 'Breaks', render: (_p, ctx) => <Breaks api={api} go={ctx.go} /> },
    { id: 'turnaround', title: 'Turnaround', render: (_p, ctx) => <Turnaround api={api} go={ctx.go} /> },
    { id: 'retention', title: 'Retention', render: () => <Retention api={api} /> },
    // The changes of the system that wait for a bank approver (R-09-77): unmasks, erasures, legal holds' releases, rebuilds.
    { id: 'consents', title: 'Consents', render: () => <Consents api={api} /> },
    // A person's upload to the File API (R-05-73), and the console's own audit trail for supervisors and bank approvers (R-05-75).
    { id: 'upload', title: 'Upload', render: (_p, ctx) => <Upload api={api} go={ctx.go} /> },
    { id: 'audit', title: 'Audit', render: () => <AuditTrail title="Operations audit" load={(q) => api.audit(q)} /> },
    // The bank's registers (R-05-62): makers propose, a different bank approver consents.
    { id: 'registers', title: 'Registers', render: (path, ctx) => <RegistersSection api={registersClient(auth)} path={path} go={ctx.go} /> },
  );
  if (hosts.service) sections.push({ id: 'service', title: 'Service', render: (path) => <ServiceGate auth={auth} path={path} /> });
  if (hosts.testbench) sections.push({ id: 'testbench', title: 'Test bench', render: (path, ctx) => <TestBenchGate auth={auth} path={path} go={ctx.go} /> });
  return <Shell auth={auth} sections={sections} title={hosts.ops ? 'TransactHub Operations' : hosts.service ? 'TransactHub Service' : 'TransactHub Test bench'} status={signedIn && hosts.ops ? <><LagBadge api={api} /> <RatesBadge api={api} /></> : null} />;
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
  const render = () => root.render(<StrictMode><Themed><App key={auth.current()?.subject ?? 'none'} auth={auth} hosts={hosts} /></Themed></StrictMode>);
  render();
  window.addEventListener('th-auth-changed', render);
})();
