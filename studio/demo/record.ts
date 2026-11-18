import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as os from 'node:os';
import * as path from 'node:path';
import { startServer, STUDIO, type Server } from '../test/blackbox/server.js';

// Records a narrated demo of the studio: a real browser session on a fresh workspace, driven click
// by click, with a visible cursor and captions, and a voice-over laid at the time each scene starts.
//
//   npx tsx demo/record.ts concepts|build [--out ../docs/demo]
//
// The voice comes from a text-to-speech command: TH_TTS="<python> <say.py> <model> <voices> <voice>"
// reads the text on stdin and writes a WAV to the path given last (see demo/README.md). Without it,
// the video is recorded silent, with captions.

const require = createRequire(import.meta.url);
export type Scene = { say: string; do?: (d: Director) => Promise<void> };

export class Director {
  page: any;
  server!: Server;
  private t0 = 0;
  marks: { at: number; wav?: string; seconds: number; text: string }[] = [];

  constructor(private ttsCmd: string | undefined, private cache: string) {}

  now() {
    return (Date.now() - this.t0) / 1000;
  }
  start(page: any) {
    this.page = page;
    this.t0 = Date.now();
  }

  /** Synthesise a line of narration once; cached by its text. */
  voice(text: string): { wav?: string; seconds: number } {
    const words = text.split(/\s+/).length;
    if (!this.ttsCmd) return { seconds: Math.max(2.5, words / 2.6) };
    const id = createHash('sha256').update(text).digest('hex').slice(0, 16);
    const wav = path.join(this.cache, `${id}.wav`);
    const meta = `${wav}.json`;
    if (!fs.existsSync(meta)) {
      const [cmd, ...args] = this.ttsCmd.split(' ');
      const r = spawnSync(cmd, [...args, wav], { input: text, encoding: 'utf8' });
      if (r.status !== 0) throw new Error(`Text to speech failed: ${r.stderr}`);
      fs.writeFileSync(meta, r.stdout.trim().split('\n').pop()!);
    }
    return { wav, seconds: JSON.parse(fs.readFileSync(meta, 'utf8')).seconds };
  }

  /** One scene: the caption and voice start, the actions run, and the scene lasts as long as both. */
  async scene(s: Scene) {
    const v = this.voice(s.say);
    const at = this.now();
    this.marks.push({ at, wav: v.wav, seconds: v.seconds, text: s.say });
    await this.page.evaluate((t: string) => (window as any).__caption?.(t), s.say);
    await s.do?.(this);
    const left = at + v.seconds + 0.6 - this.now();
    if (left > 0) await this.page.waitForTimeout(left * 1000);
  }

  // ------------------------------------------------------------- actions a viewer can follow

  async go(hash: string) {
    await this.page.goto(`${this.server.url}/#/${hash}`);
    await this.page.waitForLoadState('networkidle');
    await this.page.waitForTimeout(400);
  }
  private async moveTo(loc: any) {
    await loc.scrollIntoViewIfNeeded();
    const b = await loc.boundingBox();
    if (!b) return;
    await this.page.mouse.move(b.x + Math.min(b.width / 2, 60), b.y + b.height / 2, { steps: 18 });
    await this.page.waitForTimeout(180);
  }
  async click(loc: any) {
    await this.moveTo(loc);
    await loc.click();
    await this.page.waitForTimeout(350);
  }
  async select(loc: any, value: any) {
    await this.moveTo(loc);
    await loc.selectOption(value);
    await this.page.waitForTimeout(300);
  }
  async type(loc: any, text: string, delay = 45) {
    await this.moveTo(loc);
    await loc.click();
    await loc.fill('');
    await loc.pressSequentially(text, { delay });
    await this.page.waitForTimeout(250);
  }
  async paste(loc: any, text: string) {
    await this.moveTo(loc);
    await loc.fill(text);
    await this.page.waitForTimeout(400);
  }
  async hover(loc: any, ms = 600) {
    await this.moveTo(loc);
    await this.page.waitForTimeout(ms);
  }
  async scroll(px: number) {
    await this.page.mouse.wheel(0, px);
    await this.page.waitForTimeout(500);
  }
  async as(tenant: string, actor: string) {
    await this.select(this.page.getByLabel('Tenant', { exact: true }), tenant);
    await this.select(this.page.getByLabel('Acting as'), actor);
  }
  /** A terminal panel over the page, showing real command output. */
  async terminal(command: string, output: string) {
    await this.page.evaluate(([c, o]: string[]) => (window as any).__terminal?.(c, o), [command, output]);
  }
  async closeTerminal() {
    await this.page.evaluate(() => (window as any).__terminal?.());
  }
}

// The cursor, click ripple, captions and terminal, drawn into every page the browser opens.
const OVERLAY = `
(() => {
  const css = document.createElement('style');
  css.textContent = \`
    #__cur { position: fixed; z-index: 2147483647; width: 18px; height: 18px; margin: -3px 0 0 -3px; pointer-events: none;
      background: no-repeat url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='18' height='18'><path d='M2 1 L2 15 L6 11 L9 17 L11 16 L8 10 L14 10 Z' fill='white' stroke='black' stroke-width='1.2'/></svg>"); transition: left .02s, top .02s; }
    .__ripple { position: fixed; z-index: 2147483646; width: 34px; height: 34px; margin: -17px 0 0 -17px; border-radius: 50%;
      border: 3px solid #0f6b5c; pointer-events: none; animation: __r .55s ease-out forwards; }
    @keyframes __r { from { transform: scale(.3); opacity: 1 } to { transform: scale(1.4); opacity: 0 } }
    #__cap { position: fixed; z-index: 2147483645; left: 50%; bottom: 18px; transform: translateX(-50%); max-width: 1000px; width: calc(100% - 80px);
      background: rgba(15, 20, 26, .86); color: #fff; font: 500 19px/1.4 system-ui, sans-serif; padding: 10px 18px; border-radius: 6px; pointer-events: none; text-align: center; }
    #__cap:empty { display: none; }
    #__term { position: fixed; z-index: 2147483644; left: 40px; right: 40px; top: 60px; bottom: 110px; background: #0d1117; color: #d6e2ee;
      font: 13px/1.5 ui-monospace, Menlo, Consolas, monospace; padding: 16px 20px; border-radius: 8px; overflow: hidden; white-space: pre-wrap; box-shadow: 0 10px 40px rgba(0,0,0,.4); }
    #__term b { color: #4fc2ad; }
  \`;
  const add = () => {
    if (document.getElementById('__cur')) return;
    document.head.appendChild(css);
    const cur = document.createElement('div'); cur.id = '__cur'; cur.style.left = '-40px'; cur.style.top = '-40px'; document.body.appendChild(cur);
    const cap = document.createElement('div'); cap.id = '__cap'; document.body.appendChild(cap);
    addEventListener('mousemove', (e) => { cur.style.left = e.clientX + 'px'; cur.style.top = e.clientY + 'px'; }, true);
    addEventListener('mousedown', (e) => { const r = document.createElement('div'); r.className = '__ripple'; r.style.left = e.clientX + 'px'; r.style.top = e.clientY + 'px'; document.body.appendChild(r); setTimeout(() => r.remove(), 600); }, true);
    window.__caption = (t) => { cap.textContent = t || ''; };
    window.__terminal = (c, o) => {
      let t = document.getElementById('__term');
      if (!c) { t && t.remove(); return; }
      if (!t) { t = document.createElement('div'); t.id = '__term'; document.body.appendChild(t); }
      t.innerHTML = '';
      const p = document.createElement('b'); p.textContent = '$ ' + c + '\\n\\n'; t.appendChild(p);
      t.appendChild(document.createTextNode(o));
    };
  };
  if (document.body) add(); else addEventListener('DOMContentLoaded', add);
})();
`;

export async function record(name: string, title: string, scenes: (d: Director) => Scene[], outDir: string) {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), `th-demo-${name}-`));
  const cache = path.join(outDir, '.voice');
  fs.mkdirSync(cache, { recursive: true });
  const d = new Director(process.env.TH_TTS, cache);
  // Voice every line first, so that a failing voice stops the run before the recording.
  const list = scenes(d);
  for (const s of list) d.voice(s.say);

  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO, stdio: 'inherit' });
  d.server = await startServer();
  const browser = await require('playwright').chromium.launch();
  const size = { width: 1280, height: 720 };
  const context = await browser.newContext({ viewport: size, recordVideo: { dir: work, size }, acceptDownloads: true });
  await context.addInitScript(OVERLAY);
  const page = await context.newPage();
  d.start(page);
  let length = 0;
  try {
    for (const [i, s] of list.entries()) {
      process.stdout.write(`scene ${i + 1}/${list.length} at ${d.now().toFixed(1)}s\n`);
      await d.scene(s);
    }
    await page.waitForTimeout(1500);
    length = d.now();
  } finally {
    await context.close();
    await browser.close();
    d.server.stop();
  }
  const webm = fs.readdirSync(work).find((f) => f.endsWith('.webm'))!;
  const video = path.join(work, webm);

  // The voice: each line at the time its scene started, mixed into one track.
  const out = path.join(outDir, `${name}.mp4`);
  const voiced = d.marks.filter((m) => m.wav);
  const args = ['-y', '-i', video];
  for (const m of voiced) args.push('-i', m.wav!);
  if (voiced.length) {
    const delays = voiced.map((m, i) => `[${i + 1}:a]adelay=${Math.round(m.at * 1000)}|${Math.round(m.at * 1000)}[a${i}]`);
    const mix = `${voiced.map((_, i) => `[a${i}]`).join('')}amix=inputs=${voiced.length}:normalize=0,apad[aout]`;
    args.push('-filter_complex', [...delays, mix].join(';'), '-map', '0:v', '-map', '[aout]', '-shortest');
  }
  args.push('-c:v', 'libx264', '-preset', 'medium', '-crf', '26', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-metadata', `title=${title}`);
  if (voiced.length) args.push('-c:a', 'aac', '-b:a', '128k');
  args.push(out);
  const ff = spawnSync('ffmpeg', args, { encoding: 'utf8' });
  if (ff.status !== 0) throw new Error(`ffmpeg failed: ${ff.stderr.slice(-2000)}`);

  // Subtitles beside the video, from the same lines and times.
  const ts = (s: number) => new Date(Math.max(0, s) * 1000).toISOString().slice(11, 23).replace('.', ',');
  const srt = d.marks.map((m, i) => `${i + 1}\n${ts(m.at)} --> ${ts(m.at + m.seconds)}\n${m.text}\n`).join('\n');
  fs.writeFileSync(out.replace(/\.mp4$/, '.srt'), srt);
  console.log(`${out}: ${(fs.statSync(out).size / 1e6).toFixed(1)} MB, ${Math.floor(length / 60)} min ${Math.round(length % 60)} s, ${list.length} scenes`);
}

// ------------------------------------------------------------------------------- entry
const which = process.argv[2];
if (which) {
  const outArg = process.argv.indexOf('--out');
  const outDir = path.resolve(outArg > 0 ? process.argv[outArg + 1] : path.join(STUDIO, '..', 'docs', 'demo'));
  fs.mkdirSync(outDir, { recursive: true });
  const mod = which === 'concepts' ? await import('./concepts.js') : which === 'build' ? await import('./build.js') : undefined;
  if (!mod) {
    console.error('Usage: npx tsx demo/record.ts concepts|build [--out dir]');
    process.exit(2);
  }
  await record(mod.NAME, mod.TITLE, mod.scenes, outDir);
}
