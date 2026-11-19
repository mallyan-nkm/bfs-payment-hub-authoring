import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

// Black-box harness: the studio as a separate process, reached only over HTTP.

export const STUDIO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

export interface Server { url: string; dataRoot: string; child: ChildProcess; stop(): void }

/** Model credentials a test server never gets unless asked for: a test must not spend on a real key. */
const AI_KEYS = ['ANTHROPIC_API_KEY', 'TH_ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'ANTHROPIC_PROFILE'];

export async function startServer(extraEnv: Record<string, string> = {}, opts: { keepAiKeys?: boolean } = {}): Promise<Server> {
  const dataRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'th-bb-'));
  const port = 5100 + Math.floor(Math.random() * 800);
  const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/server/main.ts'], { cwd: STUDIO, env: { ...process.env, ...(opts.keepAiKeys ? {} : Object.fromEntries(AI_KEYS.map((k) => [k, '']))), PORT: String(port), TH_DATA_ROOT: dataRoot, ...extraEnv }, stdio: ['ignore', 'pipe', 'pipe'] });
  // The server's output is read, so a full pipe never blocks it; TH_BB_SERVER_LOG keeps it in a file.
  const sink = process.env.TH_BB_SERVER_LOG ? fs.createWriteStream(process.env.TH_BB_SERVER_LOG, { flags: 'a' }) : undefined;
  child.stdout?.on('data', (d) => sink?.write(d));
  child.stderr?.on('data', (d) => sink?.write(d));
  child.on('exit', (code, signal) => sink?.write(`\n[server exited ${code ?? ''} ${signal ?? ''}]\n`));
  const url = `http://127.0.0.1:${port}`;
  for (let i = 0; i < 200; i++) {
    try {
      if ((await fetch(`${url}/api/meta`)).ok) break;
    } catch {
      // not up yet
    }
    await new Promise((r) => setTimeout(r, 100));
  }
  return { url, dataRoot, child, stop: () => child.kill() };
}

export function client(base: string) {
  const call = async (method: string, p: string, body?: any) => {
    let r: Response | undefined;
    // A test that blocks this process (a synchronous spawn) can outlast the server's keep-alive; the
    // next request then finds its pooled connection closed. A GET is safe to send once more.
    for (let tries = 0; !r; tries++) {
      try {
        r = await fetch(base + p, { method, headers: body ? { 'content-type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
      } catch (e: any) {
        if (method === 'GET' && tries === 0 && e.cause?.code === 'UND_ERR_SOCKET') continue;
        throw new Error(`${method} ${p}: ${e.message}${e.cause ? ` (${e.cause.code ?? ''} ${e.cause.message ?? e.cause})` : ''}`);
      }
    }
    const type = r.headers.get('content-type') ?? '';
    const data = type.includes('json') ? await r.json() : Buffer.from(await r.arrayBuffer());
    return { status: r.status, data: data as any };
  };
  return {
    get: (p: string) => call('GET', p),
    post: (p: string, body?: any) => call('POST', p, body ?? {}),
    del: (p: string) => call('DELETE', p),
  };
}
