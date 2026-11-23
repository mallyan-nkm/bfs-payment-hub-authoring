import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

// Where a script-behind runs (R-08-20, Issue I-119): in a worker process of its own with nothing of the studio in it.
//
//   - an empty environment: no token, no key, no setting of the studio;
//   - TypeScript: Node's permission model with no grant (no file read or write, no child process, no worker, no addon, no WASI),
//     a heap limit (TH_SCRIPT_MEMORY_MB, 256), and only `script-worker.cjs` loaded;
//   - Python: `python3 -I -S` under the harness's audit hook and, on Linux, a Landlock ruleset that refuses every file and every
//     TCP connection, with an address space limit (TH_SCRIPT_MEMORY_MB) and no file it may write (RLIMIT_FSIZE 0);
//   - on Linux, both in a user and network namespace of their own (`unshare`), so no network is reachable at all.
//
// The worker talks JSON over its standard input and output only. A platform without namespaces (macOS, Windows) keeps the
// rest; `isolation()` says which layers hold, and the deployment guide says what that means for bank authoring.

const here = path.dirname(fileURLToPath(typeof __filename !== 'undefined' ? 'file://' + __filename : import.meta.url));

/** The worker program: next to this module, or where the exported worker's runtime keeps it (TH_SCRIPT_WORKER). */
export const SCRIPT_WORKER = process.env.TH_SCRIPT_WORKER ?? path.join(here, 'script-worker.cjs');

/** The memory one script may use, in MB: the TypeScript worker's heap, the Python interpreter's address space. */
export const scriptMemoryMb = () => Math.max(32, Number(process.env.TH_SCRIPT_MEMORY_MB ?? 256) || 256);
/** The longest one script run may take, waiting on its ports included, before its worker is stopped. */
export const scriptWallMs = () => Math.max(1000, Number(process.env.TH_SCRIPT_WALL_MS ?? 120000) || 120000);

let namespace: string[] | null | undefined;

/** The `unshare` prefix that gives a process a user and network namespace of its own, where this platform allows it. */
export function namespacePrefix(): string[] | null {
  if (namespace !== undefined) return namespace;
  namespace = null;
  if (process.platform !== 'linux' || process.env.TH_SCRIPT_NAMESPACE === '0') return namespace;
  for (const map of ['--map-current-user', '--map-root-user']) {
    const prefix = ['unshare', '--user', map, '--net', '--pid', '--kill-child'];
    const r = spawnSync(prefix[0], [...prefix.slice(1), process.execPath, '-e', '0'], { stdio: 'ignore', env: {}, timeout: 5000 });
    if (r.status === 0) {
      namespace = [resolveCommand('unshare') ?? 'unshare', ...prefix.slice(1)];
      break;
    }
  }
  return namespace;
}

/** A command's absolute path, so a child with an empty environment needs no PATH. */
export function resolveCommand(cmd: string): string | undefined {
  if (path.isAbsolute(cmd)) return cmd;
  for (const d of (process.env.PATH ?? '').split(path.delimiter)) {
    for (const ext of process.platform === 'win32' ? ['.exe', ''] : ['']) {
      const f = path.join(d, cmd + ext);
      try {
        if (fs.statSync(f).isFile()) return fs.realpathSync(f);
      } catch {
        /* not here */
      }
    }
  }
  return undefined;
}

/** How a TypeScript script worker is started: the command, its arguments and its (empty) environment. */
export function scriptWorkerSpawn(program = SCRIPT_WORKER): { command: string; args: string[]; env: Record<string, string>; cwd: string } {
  const node = [process.execPath, '--permission', `--max-old-space-size=${scriptMemoryMb()}`, '--disallow-code-generation-from-strings', '--no-addons', '--no-warnings', program];
  const ns = namespacePrefix();
  const all = ns ? [...ns, ...node] : node;
  return { command: all[0], args: all.slice(1), env: {}, cwd: os.tmpdir() };
}

/** What isolates a script-behind on this machine, as the studio reports it (`studio check`, the bench). */
export function isolation(): { process: true; environment: 'empty'; files: string; network: string; memoryMb: number } {
  return {
    process: true,
    environment: 'empty',
    files: process.platform === 'linux' ? 'none (Node permission model; Python Landlock and audit hook)' : 'none (Node permission model; Python audit hook)',
    network: namespacePrefix() ? 'none (network namespace)' : process.platform === 'linux' ? 'TypeScript: not separated; Python: no TCP (Landlock)' : 'not separated on this platform',
    memoryMb: scriptMemoryMb(),
  };
}

// ------------------------------------------------------------------ the pool of TypeScript workers

interface Worker {
  child: ChildProcess;
  partition: string;
  busy: boolean;
  dead: boolean;
  onLine?: (line: string) => void;
  onExit?: (why: string) => void;
  idleTimer?: NodeJS.Timeout;
  buf: string;
}

const workers: Worker[] = [];
const waiting: { partition: string; take: (w: Worker) => void }[] = [];
const maxWorkers = () => Math.max(1, Number(process.env.TH_SCRIPT_WORKERS ?? Math.min(4, os.cpus().length || 1)) || 1);

function setRef(w: Worker, on: boolean) {
  const f = on ? 'ref' : 'unref';
  (w.child as any)[f]?.();
  for (const s of [w.child.stdin, w.child.stdout, w.child.stderr]) (s as any)?.[f]?.();
}

function startWorker(partition: string): Worker {
  const spec = scriptWorkerSpawn();
  const child = spawn(spec.command, spec.args, { env: spec.env, cwd: spec.cwd, stdio: ['pipe', 'pipe', 'pipe'] });
  const w: Worker = { child, partition, busy: false, dead: false, buf: '' };
  let stderr = '';
  child.stderr!.on('data', (d) => (stderr = (stderr + d).slice(-2000)));
  child.stdout!.setEncoding('utf8');
  child.stdout!.on('data', (d: string) => {
    w.buf += d;
    let i;
    while ((i = w.buf.indexOf('\n')) >= 0) {
      const line = w.buf.slice(0, i);
      w.buf = w.buf.slice(i + 1);
      w.onLine?.(line);
    }
  });
  const gone = (why: string) => {
    if (w.dead) return;
    w.dead = true;
    const i = workers.indexOf(w);
    if (i >= 0) workers.splice(i, 1);
    clearTimeout(w.idleTimer);
    w.onExit?.(why);
    pump();
  };
  child.on('error', (e) => gone(`the script worker could not start: ${e.message}`));
  child.on('exit', (code, signal) => gone(/heap|memory/i.test(stderr) ? 'the script used more memory than it may' : `the script worker stopped (${signal ?? code})${stderr.trim() ? ': ' + stderr.trim().split('\n').slice(-1)[0] : ''}`));
  child.stdin!.on('error', () => undefined);
  workers.push(w);
  return w;
}

function kill(w: Worker) {
  try {
    w.child.kill('SIGKILL');
  } catch {
    /* gone already */
  }
}

/** Hand idle workers to those waiting: one of the same partition first, else a new one, else an idle one of another partition replaced. */
function pump() {
  while (waiting.length) {
    const next = waiting[0];
    let w = workers.find((x) => !x.busy && !x.dead && x.partition === next.partition);
    if (!w && workers.length < maxWorkers()) w = startWorker(next.partition);
    if (!w) {
      const other = workers.find((x) => !x.busy && !x.dead);
      if (!other) return;
      other.dead = true;
      workers.splice(workers.indexOf(other), 1);
      kill(other);
      w = startWorker(next.partition);
    }
    waiting.shift();
    w.busy = true;
    clearTimeout(w.idleTimer);
    setRef(w, true);
    next.take(w);
  }
}

/**
 * A worker for one run. A worker serves one partition (a tenant) only: one bank's scripts never run in a process another bank's
 * ran in. It is given back with `release`, or stopped with `discard`.
 */
export function acquireWorker(partition: string): Promise<{ send(msg: any): void; onLine(f: (line: string) => void): void; onExit(f: (why: string) => void): void; release(): void; discard(): void }> {
  return new Promise((resolve) => {
    waiting.push({
      partition,
      take: (w) => resolve({
        send: (msg) => {
          if (!w.dead) w.child.stdin!.write(JSON.stringify(msg) + '\n');
        },
        onLine: (f) => (w.onLine = f),
        onExit: (f) => (w.onExit = f),
        release: () => {
          w.onLine = undefined;
          w.onExit = undefined;
          w.busy = false;
          if (w.dead) return pump();
          setRef(w, false);
          w.idleTimer = setTimeout(() => {
            if (!w.busy) kill(w);
          }, 30000);
          w.idleTimer.unref();
          pump();
        },
        discard: () => {
          w.onLine = undefined;
          w.onExit = undefined;
          kill(w);
        },
      }),
    });
    pump();
  });
}

/** Stop every idle worker (a command that ends, a test). */
export function stopWorkers() {
  for (const w of [...workers]) if (!w.busy) kill(w);
}
