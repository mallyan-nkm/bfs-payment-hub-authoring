import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as http from 'node:http';
import * as net from 'node:net';
import * as os from 'node:os';
import * as path from 'node:path';
import { afterAll, describe, expect, it } from 'vitest';
import { FakeGitHub } from './support/fake-github.js';
import { STUDIO_ROOT } from '../src/env.js';

// Issue I-408 (R-09-306): a request body arrives in chunks, and a chunk may end inside a character of two or more
// bytes. A server that decodes each chunk on its own (`raw += chunk`) turns that character into replacement
// characters, so it keeps other bytes than were sent: the fake GitHub stored a worker bundle (which has a few such
// characters) as other text than the studio had signed, now and then, and the runtime refused the distribution. Each
// server here is sent a body split inside a character, its two parts apart in time, so they arrive as two chunks.

const TEXT = 'A synthetic text with a section sign § and quotes ‘’ that must arrive whole.';
const blobSha = (t: string) => { const b = Buffer.from(t, 'utf8'); return createHash('sha1').update(Buffer.concat([Buffer.from(`blob ${b.length}\0`), b])).digest('hex'); };

/** A POST whose body is sent in two writes, split inside the first character of more than one byte. */
function splitPost(url: string, body: string, headers: Record<string, string> = {}): Promise<{ status: number; text: string }> {
  const bytes = Buffer.from(body, 'utf8');
  const at = bytes.indexOf(Buffer.from('§', 'utf8')) + 1;
  expect(at).toBeGreaterThan(0);
  return new Promise((resolve, reject) => {
    const req = http.request(url, { method: 'POST', headers: { 'content-type': 'application/json', 'content-length': String(bytes.length), ...headers } }, (res) => {
      const chunks: Buffer[] = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode ?? 0, text: Buffer.concat(chunks).toString('utf8') }));
    });
    req.on('error', reject);
    req.write(bytes.subarray(0, at));
    setTimeout(() => req.end(bytes.subarray(at)), 100);
  });
}

const freePort = () => new Promise<number>((resolve) => { const s = net.createServer(); s.listen(0, '127.0.0.1', () => { const p = (s.address() as any).port; s.close(() => resolve(p)); }); });

const cleanup: (() => void)[] = [];
afterAll(() => cleanup.forEach((f) => f()));

describe('a body split inside a character arrives whole (Issue I-408)', () => {
  it('the fake GitHub stores a tree entry\'s text as it was sent, so its blob SHA is the one the studio worked out', async () => {
    const f = new FakeGitHub('t');
    await f.start({ unref: true });
    cleanup.push(() => void f.stop());
    f.seed('o/r', 'main', { 'a.txt': 'a\n' });
    const r = await splitPost(`${f.url}/repos/o/r/git/trees`, JSON.stringify({ tree: [{ path: 'x.txt', mode: '100644', type: 'blob', content: TEXT }] }), { authorization: 'Bearer t' });
    expect(r.status, r.text).toBe(201);
    const tree = JSON.parse(r.text).sha;
    const t = await (await fetch(`${f.url}/repos/o/r/git/trees/${tree}`, { headers: { authorization: 'Bearer t' } })).json() as any;
    expect(t.tree.find((e: any) => e.path === 'x.txt').sha).toBe(blobSha(TEXT));
  });

  it('a deployed worker reads an attempt\'s body as it was sent', async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'th-body-'));
    cleanup.push(() => fs.rmSync(dir, { recursive: true, force: true }));
    fs.writeFileSync(path.join(dir, 'manifest.json'), JSON.stringify({ queue: 'th-test', tasks: ['p.enrich.synthetic.v1@1.0.0'] }));
    const port = await freePort();
    const child = spawn(path.join(STUDIO_ROOT, 'node_modules', '.bin', 'tsx'), [path.join(STUDIO_ROOT, 'src', 'worker', 'main.ts'), '--dir', dir, '--port', String(port)], { cwd: STUDIO_ROOT, env: { PATH: process.env.PATH }, stdio: ['ignore', 'pipe', 'pipe'] });
    cleanup.push(() => child.kill());
    for (let i = 0; i < 100; i++) { try { if ((await fetch(`http://127.0.0.1:${port}/health/live`)).ok) break; } catch { /* not yet */ } await new Promise((r) => setTimeout(r, 200)); }
    // A task the queue does not serve is refused with its name, as the worker read it from the body.
    const activity = `p.enrich.${TEXT}`;
    const r = await splitPost(`http://127.0.0.1:${port}/attempt`, JSON.stringify({ activity, release: '1.0.0' }));
    expect(r.status, r.text).toBe(200);
    expect(JSON.parse(r.text)).toMatchObject({ kind: 'error', name: 'NotOnThisQueue', message: `${activity}@1.0.0 is not registered on queue th-test` });
  }, 60000);
});
