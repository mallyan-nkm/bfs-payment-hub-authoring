// The runtime's load harness (R-09-38, I-68; docs/runtime/LOAD-TEST.md). It is not a studio test: the studio offers
// functional tests only (spec/CLAUDE.md). It drives synthetic WPS salary files through the runtime on Temporal, as a
// deployment runs them: the factory's corporate payment workflow with the WPS leg, exported for the example bank with
// the Temporal binding, its four workers as separate processes, the front door's intake for each file, then every
// file side by side on one Temporal harness (LoadRunner), its payloads in the store when TH_STORE_PROVIDER is set.
//
//   cd studio && npx tsx ../runtime/load/run.ts --files 40 --lines 5 --concurrency 8 [--ack PT5S] [--label name] [--out result.json]
//
// Environment: TH_TEMPORAL_ADDRESS (the server, runtime/load/temporal.sh), TH_STORE_PROVIDER and TH_STORE_CONNECTION
// (the store; without them the payloads stay in memory), TH_SIGNING_KEY and TH_TRUSTED_SIGNERS (the package is signed
// and the deploy runner verifies it, as for any deployment), TH_LOAD_CONTAINERS (docker containers whose CPU and IO are
// measured, as name=container,name=container; TH_LOAD_DB_PASSWORD, their test-only sa or postgres password, to read their IO
// counters), TH_LOAD_TEMPORAL_PID (the server's pid, for its CPU).
//
// What it measures: wall time, files per minute, lines (payments) per second, per-file latency (p50, p95, p99), the
// outcome of every file, the CPU seconds of each process kind and of each database container, the database's IO.
// Every value in a file is synthetic. The scheme's acknowledgement comes after --ack (the factory simulator says
// PT10M; a load run shortens it, or it would measure the wait): the setting is printed with the result.

import { execFileSync } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { openReadEnv } from '../../studio/src/env.js';
import { exportPackage, signedZip } from '../../studio/src/export/exporter.js';
import { unzip } from '../../studio/src/export/zip.js';
import { deploy } from '../../studio/src/deploy/deploy.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const studioRoot = path.resolve(here, '..', '..', 'studio');
const argv = process.argv.slice(2);
const opt = (k: string, d?: string) => { const i = argv.indexOf(`--${k}`); return i >= 0 ? argv[i + 1] : d; };
const FILES = Number(opt('files', '10'));
const LINES = Number(opt('lines', '5'));
const CONCURRENCY = Number(opt('concurrency', '4'));
const ACK = opt('ack', 'PT5S')!;
const LABEL = opt('label', `load-${FILES}x${LINES}-c${CONCURRENCY}`)!;
const TENANT = 'example-bank';
const WORKFLOW = 'p.wf.corporate.v1';
const BENCH = [
  ...['core-banking', 'sanctions', 'fraud'].map((p) => ({ port: `p.port.${p}.v1`, simulator: `p.sim.${p}.v1`, scenario: 'accept' })),
  { port: 'uae.wps.port.wps.v1', simulator: 'uae.wps.sim.wps-outward.v1', scenario: 'accept' },
  { port: 'uae.p.port.calendar.v1', simulator: 'uae.p.sim.calendar.v1', scenario: 'accept' },
];

// ------------------------------------------------------------------ synthetic salary files

/** A valid UAE IBAN for bank code 033 and a 16-digit account number (ISO 13616 check digits). */
function iban(account: number): string {
  const bban = `033${String(account).padStart(16, '0')}`;
  const digits = `${bban}101400`; // AE00 moved to the end: A=10, E=14
  let mod = 0;
  for (const ch of digits) mod = (mod * 10 + Number(ch)) % 97;
  return `AE${String(98 - mod).padStart(2, '0')}${bban}`;
}

/** One salary file: n lines for one synthetic employer, with a file name and reference of its own. */
function salaryFile(n: number, lines: number): string {
  const hhmm = (() => { const m = (510 + n) % 1440; return `${String(Math.floor(m / 60)).padStart(2, '0')}${String(m % 60).padStart(2, '0')}`; })();
  const seq = String(Math.floor(n / 1440) % 100).padStart(2, '0');
  const edr: string[] = [];
  for (let i = 0; i < lines; i++) {
    const id = String(90000000000000 + n * 100000 + i).padStart(14, '0');
    edr.push(`EDR,${id},203320101,${iban(7000000000000000 + n * 100000 + i)},2026-10-01,2026-10-31,31,1000.00,0.00,0`);
  }
  const total = (lines * 1000).toFixed(2);
  return [`# file: 0000001234567261117${hhmm}${seq}.sif`, `SCR,0000001234567,302620122,2026-11-17,${hhmm},102026,${lines},${total},AED,LOAD-${n}`, ...edr].join('\n') + '\n';
}

// ------------------------------------------------------------------ what the machine did

const sh = (cmd: string, args: string[]) => { try { return execFileSync(cmd, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }); } catch { return ''; } };
const containers = Object.fromEntries((process.env.TH_LOAD_CONTAINERS ?? '').split(',').filter(Boolean).map((kv) => kv.split('=') as [string, string]));

/**
 * CPU seconds of a database container (its cgroup on the host, v1 or v2), and the IO its database reports: SQL Server's
 * sys.dm_io_virtual_file_stats, PostgreSQL's pg_stat_io (blocks of 8 KiB) and its WAL bytes. The container's own block
 * IO counters read zero on this host's overlay storage, so the databases' figures are used.
 */
function cgroup(container: string) {
  const id = sh('docker', ['inspect', '-f', '{{.Id}}', container]).trim();
  let cpuSeconds = NaN;
  for (const [f, scale, re] of [[`/sys/fs/cgroup/cpuacct/docker/${id}/cpuacct.usage`, 1e9, /^(\d+)/], [`/sys/fs/cgroup/system.slice/docker-${id}.scope/cpu.stat`, 1e6, /usage_usec (\d+)/]] as const) {
    try { const m = re.exec(fs.readFileSync(f, 'utf8')); if (m) { cpuSeconds = Number(m[1]) / scale; break; } } catch { /* not this layout */ }
  }
  const password = process.env.TH_LOAD_DB_PASSWORD ?? 'Th-Load-Only-1234';
  let readBytes = NaN;
  let writtenBytes = NaN;
  const image = sh('docker', ['inspect', '-f', '{{.Config.Image}}', container]);
  if (image.includes('mssql')) {
    const out = sh('docker', ['exec', container, '/opt/mssql-tools18/bin/sqlcmd', '-C', '-S', 'localhost', '-U', 'sa', '-P', password, '-h', '-1', '-W', '-s', ',', '-Q',
      'SET NOCOUNT ON; SELECT SUM(num_of_bytes_read), SUM(num_of_bytes_written) FROM sys.dm_io_virtual_file_stats(NULL,NULL)']).trim().split(',');
    readBytes = Number(out[0]);
    writtenBytes = Number(out[1]);
  } else if (image.includes('postgres')) {
    const out = sh('docker', ['exec', container, 'psql', '-U', 'postgres', '-At', '-c', 'select coalesce(sum(reads),0)*8192, coalesce(sum(writes),0)*8192 + (select wal_bytes from pg_stat_wal) from pg_stat_io']).trim().split('|');
    readBytes = Number(out[0]);
    writtenBytes = Number(out[1]);
  }
  return { cpuSeconds, readBytes, writtenBytes };
}

/** CPU seconds of one process, from /proc (utime + stime). */
function procCpu(pid: number): number | undefined {
  try {
    const f = fs.readFileSync(`/proc/${pid}/stat`, 'utf8');
    const parts = f.slice(f.lastIndexOf(')') + 2).split(' ');
    return (Number(parts[11]) + Number(parts[12])) / (Number(sh('getconf', ['CLK_TCK']).trim()) || 100);
  } catch {
    return undefined;
  }
}

/** The processes of this run, by kind: the harness, the workers, the Temporal server. */
let packageDir = '';
function kindOf(args: string): string | undefined {
  // Only this run's: other deployments on the machine are not counted.
  if (args.includes('TransactHub.TemporalHarness') && args.includes('--load')) return 'harness (.NET, Temporal workers)';
  if (args.includes('worker.cjs') && packageDir && args.includes(packageDir)) return 'activity workers (Node)';
  if (args.includes('temporal-server')) return 'temporal server';
  return undefined;
}

const seen = new Map<number, { kind: string; cpu: number; rssMb: number }>();
function sample() {
  for (const line of sh('ps', ['-eo', 'pid=,rss=,args=']).split('\n')) {
    const m = /^\s*(\d+)\s+(\d+)\s+(.*)$/.exec(line);
    if (!m) continue;
    const kind = kindOf(m[3]);
    if (!kind) continue;
    if (kind === 'temporal server' && process.env.TH_LOAD_TEMPORAL_PID && m[1] !== process.env.TH_LOAD_TEMPORAL_PID) continue;
    const pid = Number(m[1]);
    const cpu = procCpu(pid);
    if (cpu === undefined) continue;
    const prev = seen.get(pid);
    seen.set(pid, { kind, cpu, rssMb: Math.max(prev?.rssMb ?? 0, Number(m[2]) / 1024) });
  }
}

const pct = (xs: number[], p: number) => {
  if (!xs.length) return NaN;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil((p / 100) * s.length) - 1)];
};

// ------------------------------------------------------------------ the run

async function main() {
  if (!process.env.TH_TEMPORAL_ADDRESS) throw new Error('Set TH_TEMPORAL_ADDRESS to the Temporal server of the run (runtime/load/temporal.sh)');
  const { env, stop } = await openReadEnv();
  const exported = exportPackage(env, { workflows: [WORKFLOW], target: 'temporal', withSimulators: true, tenant: TENANT });
  await stop();
  // The scheme's acknowledgement after --ack, in the deployed simulator only (the factory's says PT10M), before the
  // package is signed: the deploy runner verifies every file against the signature (TH_SIGNING_KEY, TH_TRUSTED_SIGNERS).
  const files = { ...exported.files };
  for (const k of Object.keys(files)) if (k.endsWith('uae.wps.sim.wps-outward.v1@1.0.0.yaml')) files[k] = String(files[k]).replace(/after: PT10M/g, `after: ${ACK}`);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-load-'));
  packageDir = dir;
  for (const e of unzip(signedZip({ ...exported, files }))) {
    const f = path.join(dir, e.name);
    fs.mkdirSync(path.dirname(f), { recursive: true });
    fs.writeFileSync(f, e.content);
  }
  const inbounds = Array.from({ length: FILES }, (_, i) => ({ label: `f${i + 1}`, payload: salaryFile(i + 1, LINES) }));

  const before = Object.fromEntries(Object.entries(containers).map(([k, c]) => [k, cgroup(c)]));
  const temporalCpu0 = process.env.TH_LOAD_TEMPORAL_PID ? procCpu(Number(process.env.TH_LOAD_TEMPORAL_PID)) : undefined;
  sample();
  const timer = setInterval(sample, 1000);
  const t0 = Date.now();
  let r;
  try {
    r = await deploy({
      packagePath: dir, workflow: WORKFLOW, studioRoot, keep: false,
      environment: { evaluationDate: '2026-11-17', tenant: TENANT, bench: BENCH },
      inbound: { message: 'uae.wps.sif', payload: inbounds[0].payload },
      load: { inbounds, concurrency: CONCURRENCY },
    });
  } finally {
    clearInterval(timer);
    fs.rmSync(dir, { recursive: true, force: true });
  }
  const wallMs = Date.now() - t0;
  const after = Object.fromEntries(Object.entries(containers).map(([k, c]) => [k, cgroup(c)]));
  const temporalCpu1 = process.env.TH_LOAD_TEMPORAL_PID ? procCpu(Number(process.env.TH_LOAD_TEMPORAL_PID)) : undefined;

  const runs: any[] = r.load.runs;
  const ok = runs.filter((x) => x?.outcome === 'settled');
  const ms = runs.filter(Boolean).map((x) => x.ms as number);
  const engineMs = r.load.wallMs as number;
  const intakeMs = runs.reduce((a, x) => a + (x?.intakeMs ?? 0), 0);
  const cpuByKind: Record<string, { cpuSeconds: number; processes: number; maxRssMb: number }> = {};
  for (const [pid, s] of seen) {
    if (s.kind === 'temporal server') continue;
    const k = (cpuByKind[s.kind] ??= { cpuSeconds: 0, processes: 0, maxRssMb: 0 });
    k.cpuSeconds += s.cpu;
    k.processes++;
    k.maxRssMb = Math.max(k.maxRssMb, s.rssMb);
    void pid;
  }
  if (temporalCpu0 !== undefined && temporalCpu1 !== undefined) cpuByKind['temporal server'] = { cpuSeconds: temporalCpu1 - temporalCpu0, processes: 1, maxRssMb: Math.max(0, ...[...seen.values()].filter((s) => s.kind === 'temporal server').map((s) => s.rssMb)) };
  const round = (x: number, d = 1) => Math.round(x * 10 ** d) / 10 ** d;
  const summary = {
    label: LABEL,
    at: new Date().toISOString(),
    machine: { cpus: os.cpus().length, cpuModel: os.cpus()[0]?.model, memoryGb: round(os.totalmem() / 2 ** 30), loadAverageAtEnd: os.loadavg().map((x) => round(x, 2)) },
    settings: {
      files: FILES, linesPerFile: LINES, concurrency: CONCURRENCY, schemeAck: ACK, temporal: process.env.TH_TEMPORAL_ADDRESS,
      temporalStore: process.env.TH_LOAD_TEMPORAL_STORE ?? '?', store: process.env.TH_STORE_PROVIDER ?? 'memory', workflow: WORKFLOW, tenant: TENANT,
    },
    results: {
      wallSeconds: round(wallMs / 1000), engineSeconds: round(engineMs / 1000), intakeSeconds: round(intakeMs / 1000),
      settled: ok.length, notSettled: runs.length - ok.length, outcomes: runs.reduce((a: any, x) => ((a[x?.outcome ?? 'missing'] = (a[x?.outcome ?? 'missing'] ?? 0) + 1), a), {}),
      filesPerMinute: round((ok.length / engineMs) * 60000, 2),
      linesPerSecond: round((ok.length * LINES) / (engineMs / 1000), 2),
      latencySeconds: { p50: round(pct(ms, 50) / 1000, 2), p95: round(pct(ms, 95) / 1000, 2), p99: round(pct(ms, 99) / 1000, 2), max: round(Math.max(...ms) / 1000, 2) },
      activityRuns: r.load.activityRuns,
      errors: runs.filter((x) => x?.error || x?.failed).map((x) => `${x.label}: ${x.error ?? x.failed}`).slice(0, 10),
    },
    cpu: cpuByKind,
    databases: Object.fromEntries(Object.keys(containers).map((k) => [k, {
      container: containers[k],
      cpuSeconds: round(after[k].cpuSeconds - before[k].cpuSeconds),
      readMb: round((after[k].readBytes - before[k].readBytes) / 2 ** 20),
      writtenMb: round((after[k].writtenBytes - before[k].writtenBytes) / 2 ** 20),
    }])),
    workers: r.workers.map((w: any) => ({ queue: w.queue, served: Object.values<number>(w.served ?? {}).reduce((a, b) => a + b, 0), refused: w.refused })),
  };
  if (argv.includes('--debug')) for (const l of r.engine?.log ?? []) console.error(l);
  console.log(JSON.stringify(summary, null, 2));
  if (opt('out')) fs.writeFileSync(opt('out')!, JSON.stringify({ ...summary, runs: runs.map((x) => ({ label: x?.label, lines: x?.lines, outcome: x?.outcome, ms: x?.ms, acceptMs: x?.acceptMs, intakeMs: x?.intakeMs, trace: x?.trace, failed: x?.failed, children: x?.children })) }, null, 2));
}

main().catch((e) => {
  console.error(e?.stack ?? e);
  process.exitCode = 1;
});
