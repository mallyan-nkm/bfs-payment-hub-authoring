import { spawn, type ChildProcess } from 'node:child_process';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

// Black-box harness: the studio as a separate process, reached only over HTTP.

export const STUDIO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

export interface Server { url: string; dataRoot: string; child: ChildProcess; stop(): void }

export async function startServer(): Promise<Server> {
  const dataRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'th-bb-'));
  const port = 5100 + Math.floor(Math.random() * 800);
  const child = spawn(path.join(STUDIO, 'node_modules', '.bin', 'tsx'), ['src/server/main.ts'], { cwd: STUDIO, env: { ...process.env, PORT: String(port), TH_DATA_ROOT: dataRoot }, stdio: ['ignore', 'pipe', 'pipe'] });
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
    const r = await fetch(base + p, { method, headers: body ? { 'content-type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined });
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
