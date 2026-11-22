import { it } from 'vitest';
import { freshEnv } from './helpers.js';
import { dispatch } from '../src/server/api.js';
it('scratch', async () => {

const n = Number(process.env.N ?? 100);
function iban(account: number): string {
  const bban = `033${String(account).padStart(16, '0')}`;
  const digits = `${bban}101400`;
  let mod = 0;
  for (const ch of digits) mod = (mod * 10 + Number(ch)) % 97;
  return `AE${String(98 - mod).padStart(2, '0')}${bban}`;
}
const edr: string[] = [];
for (let i = 0; i < n; i++) edr.push(`EDR,${String(90000000000000 + 100000 + i).padStart(14, '0')},203320101,${iban(7000000000000000 + 100000 + i)},2026-10-01,2026-10-31,31,1000.00,0.00,0`);
const content = ['# file: 0000001234567261117083100.sif', `SCR,0000001234567,302620122,2026-11-17,0831,102026,${n},${(n * 1000).toFixed(2)},AED,LOAD-1`, ...edr].join('\n') + '\n';
const env = freshEnv();
const s = await dispatch(env, { method: 'POST', path: '/api/samples', query: {}, body: { tenant: 'owner', actor: 'shared.designer', name: `load-${n}.sif`, content } });
console.log('sample', s.status, JSON.stringify(s.body).slice(0, 200));
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];
const r = await dispatch(env, { method: 'POST', path: '/api/bench/run', query: {}, body: { target: 'p.wf.corporate.v1', tenant: 'example-bank', inbound: `samples/load-${n}.sif`, message: 'uae.wps.sif', bench: BENCH } });
console.log(r.status, r.body.outcome, JSON.stringify(r.body.trace));
const inst = (r.body.instances ?? r.body.runs ?? []);
console.log(JSON.stringify(Object.keys(r.body)));
for (const i of r.body.instances ?? []) console.log(i.label ?? i.name, JSON.stringify(i.trace).slice(0, 800), i.failed ?? '');
console.log(JSON.stringify(r.body.detail ?? []).slice(0, 3000));
void inst;
}, 300000);
