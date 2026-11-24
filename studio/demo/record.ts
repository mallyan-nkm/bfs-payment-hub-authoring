import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import { createRequire } from 'node:module';
import * as os from 'node:os';
import * as path from 'node:path';
import { client, signInPage, startServer, STUDIO, type Server } from '../test/blackbox/server.js';

// Records a narrated demo of the studio: a real browser session on a fresh workspace, driven click
// by click, with a visible cursor, a highlight before each click, captions, chapter cards, and a
// voice-over laid at the time each scene starts.
//
//   npx tsx demo/run.ts concept|build|hands-on|... [--out dir]
//
// The voice comes from a text-to-speech command: TH_TTS="<python> <say.py> <model> <voices> <voice>"
// reads the text on stdin, writes a WAV to the path given last, and prints {"seconds": n}
// (see demo/README.md). TH_TTS_SPEED sets the speaking speed (default 1). Without TH_TTS the video is
// recorded silent, with captions. TH_DEMO_DRY=1 runs the same steps at full speed with no video and
// no voice: the training test uses it to prove every step before anything is recorded.

const require = createRequire(import.meta.url);
export type Scene = { say: string; card?: [string, string?]; do?: (d: Director) => Promise<void> };

export interface Pace {
  steps: number; // cursor movement steps
  settle: number; // ms after reaching a control
  after: number; // ms after a click or a choice
  highlight: number; // ms a control is outlined before it is used
  typeDelay: number; // ms between keys
  tail: number; // s of quiet after each scene's voice
}
export const NORMAL: Pace = { steps: 18, settle: 180, after: 350, highlight: 0, typeDelay: 45, tail: 0.6 };
export const SLOW: Pace = { steps: 40, settle: 450, after: 900, highlight: 700, typeDelay: 85, tail: 1.6 };
const DRY = process.env.TH_DEMO_DRY === '1';

export class Director {
  page: any;
  server!: Server;
  private t0 = 0;
  marks: { at: number; wav?: string; seconds: number; text: string }[] = [];
  pace: Pace;

  constructor(private ttsCmd: string | undefined, private cache: string, pace: Pace = NORMAL) {
    this.pace = DRY ? { steps: 1, settle: 0, after: 0, highlight: 0, typeDelay: 0, tail: 0 } : pace;
  }

  get dry() {
    return DRY;
  }
  now() {
    return (Date.now() - this.t0) / 1000;
  }
  /** The caption on screen: drawn again on every page a scene opens (a sign-in, another console), so it stays while the voice talks. */
  private caption = '';
  start(page: any) {
    this.page = page;
    this.t0 = Date.now();
    page.on('load', () => { if (this.caption) page.evaluate((t: string) => (window as any).__caption?.(t), this.caption).catch(() => undefined); });
  }
  async wait(ms: number) {
    if (!DRY) await this.page.waitForTimeout(ms);
  }

  /** Synthesise a line of narration once; cached by its text and speed. */
  voice(text: string): { wav?: string; seconds: number } {
    if (DRY) return { seconds: 0 };
    const words = text.split(/\s+/).length;
    if (!this.ttsCmd) return { seconds: Math.max(2.5, words / 2.4) };
    const speed = process.env.TH_TTS_SPEED ?? '1';
    const id = createHash('sha256').update(`${speed}|${text}`).digest('hex').slice(0, 16);
    const wav = path.join(this.cache, `${id}.wav`);
    const meta = `${wav}.json`;
    if (!fs.existsSync(meta)) {
      const [cmd, ...args] = this.ttsCmd.split(' ');
      const r = spawnSync(cmd, [...args, wav], { input: text, encoding: 'utf8', env: { ...process.env, TH_TTS_SPEED: speed } });
      if (r.status !== 0) throw new Error(`Text to speech failed: ${r.stderr}`);
      fs.writeFileSync(meta, r.stdout.trim().split('\n').pop()!);
    }
    return { wav, seconds: JSON.parse(fs.readFileSync(meta, 'utf8')).seconds };
  }


  /** One scene: a chapter card if any, the caption and voice, the actions; it lasts as long as both. */
  async scene(s: Scene) {
    const v = this.voice(s.say);
    const at = this.now();
    this.marks.push({ at, wav: v.wav, seconds: v.seconds, text: s.say });
    this.caption = s.say;
    if (s.card) await this.page.evaluate(([t, u]: string[]) => (window as any).__card?.(t, u), [s.card[0], s.card[1] ?? '']);
    this.caption = s.say;
    await this.page.evaluate((t: string) => (window as any).__caption?.(t), s.say);
    // A card over a scene with actions shows for a moment, then lifts so the viewer sees the clicks.
    if (s.card && s.do) {
      await this.wait(4000);
      await this.page.evaluate(() => (window as any).__card?.());
    }
    await s.do?.(this);
    const left = at + v.seconds + this.pace.tail - this.now();
    if (left > 0 && !DRY) await this.page.waitForTimeout(left * 1000);
    if (s.card && !s.do) await this.page.evaluate(() => (window as any).__card?.());
  }

  // ------------------------------------------------------------- actions a viewer can follow

  async go(hash: string) {
    // A surface that is not the signed-in role's is opened as a user whose it is (R-10-11).
    const view = hash.split(/[?/]/)[0];
    if (this.user.startsWith('example.') && ['kit', 'import'].includes(view)) await this.as('owner', 'shared.designer');
    if (!this.user.startsWith('example.') && ['tenant', 'bank'].includes(view)) await this.as('example-bank', 'example.maker');
    await this.page.goto(`${this.server.url}/#/${hash}`);
    await this.page.waitForLoadState('networkidle');
    await this.wait(this.pace.after);
  }
  private async moveTo(loc: any) {
    await loc.scrollIntoViewIfNeeded();
    if (DRY) return;
    const b = await loc.boundingBox();
    if (!b) return;
    await this.page.mouse.move(b.x + Math.min(b.width / 2, 60), b.y + b.height / 2, { steps: this.pace.steps });
    if (this.pace.highlight) {
      await this.page.evaluate((r: any) => (window as any).__mark?.(r), { x: b.x, y: b.y, w: b.width, h: b.height });
      await this.page.waitForTimeout(this.pace.highlight);
      await this.page.evaluate(() => (window as any).__mark?.());
    }
    await this.page.waitForTimeout(this.pace.settle);
  }
  async click(loc: any) {
    await this.moveTo(loc);
    await loc.click();
    await this.wait(this.pace.after);
  }
  async select(loc: any, value: any) {
    await this.moveTo(loc);
    await loc.selectOption(value);
    await this.wait(this.pace.after);
  }
  async check(loc: any) {
    await this.moveTo(loc);
    await loc.check();
    await this.wait(this.pace.after);
  }
  async type(loc: any, text: string, delay = this.pace.typeDelay) {
    await this.moveTo(loc);
    await loc.click();
    await loc.fill('');
    if (DRY) await loc.fill(text);
    else await loc.pressSequentially(text, { delay });
    await this.wait(this.pace.after / 2);
  }
  async paste(loc: any, text: string) {
    await this.moveTo(loc);
    await loc.fill(text);
    await this.wait(this.pace.after);
  }
  async hover(loc: any, ms = 900) {
    await this.moveTo(loc);
    await this.wait(ms);
  }
  /** Outline a part of the page while the voice talks about it. */
  async point(loc: any, ms = 1500) {
    await loc.scrollIntoViewIfNeeded();
    if (DRY) return;
    const b = await loc.boundingBox();
    if (!b) return;
    await this.page.mouse.move(b.x + Math.min(b.width / 2, 60), b.y + Math.min(b.height / 2, 20), { steps: this.pace.steps });
    await this.page.evaluate((r: any) => (window as any).__mark?.(r), { x: b.x, y: b.y, w: b.width, h: b.height });
    await this.page.waitForTimeout(ms);
    await this.page.evaluate(() => (window as any).__mark?.());
  }
  async scroll(px: number) {
    await this.page.mouse.wheel(0, px);
    await this.wait(this.pace.after);
  }
  /** The signed-in user. The studio takes the user from the session, so acting as another is signing in as them (R-11-32). */
  user = '';
  async as(_tenant: string, actor: string) {
    if (this.user === actor) return;
    // A sign-in that lands signed out (a busy machine, a callback raced by the app's own navigation) is tried once more.
    await signInPage(this.page, this.server.url, actor).catch(() => signInPage(this.page, this.server.url, actor));
    this.user = actor;
    await this.wait(this.pace.after);
  }
  /** Sign in as the maker of a tenant, unless the signed-in user already works for it. */
  async forTenant(tenant: string) {
    const bank = this.user.startsWith('example.');
    if (tenant === 'owner' && bank) await this.as('owner', 'shared.designer');
    if (tenant !== 'owner' && !bank) await this.as(tenant, 'example.maker');
  }
  /** An API client signed in as a user, for what a scene reads beside the page. */
  api(user = this.user) {
    return client(this.server.url, { as: user });
  }
  /**
   * The maker requests the release of the change set on screen; the approver signs in and approves it
   * as a version in Governance; the maker signs in again (R-11-31).
   */
  async release(approver: string, maker: string) {
    await this.click(this.page.getByRole('button', { name: 'Request release' }).first());
    await this.page.waitForTimeout(500);
    await this.as('', approver);
    await this.go('governance');
    await this.approveRow('in-review');
    await this.as('', maker);
  }
  /** As the approver signed in: approve the first draft row that matches, and wait for the answer. */
  async approveRow(match: string) {
    await this.click(this.page.locator('tr', { hasText: match }).first().getByRole('button', { name: 'Approve as version' }));
    for (let i = 0; i < 120; i++) {
      const t = (await this.page.locator('[role="status"]').allTextContents()).join(' ');
      if (/approved as a version/.test(t)) return;
      if (/Refused/.test(t)) throw new Error(t);
      await this.page.waitForTimeout(250);
    }
    throw new Error('No approval answered');
  }
  /**
   * A scheme licensed to the example bank in its workspace (R-11-75, Issue I-205): the bank configurator proposes the bank's next
   * lock file with it, the bank approver approves it, and the approval builds the bank's next distribution, which carries it.
   */
  async licenseScheme(scope: string) {
    const maker = this.user;
    await this.as('example-bank', 'example.maker');
    await this.go('bank');
    const box = this.page.locator('[aria-label="Schemes licensed"]');
    await this.click(box.locator('tr', { hasText: scope }).getByRole('button', { name: `License ${scope}` }));
    await this.page.waitForTimeout(800);
    await this.as('example-bank', 'example.checker');
    await this.go('bank');
    await this.click(box.getByRole('button', { name: 'Approve the licence' }));
    for (let i = 0; i < 240; i++) {
      const t = (await box.locator('[role="status"], .error').allTextContents()).join(' ');
      if (/approved in change set/.test(t)) break;
      if (/Refused/.test(t)) throw new Error(t);
      if (i === 239) throw new Error('No approval of the licence answered');
      await this.page.waitForTimeout(250);
    }
    if (maker) await this.as('example-bank', maker);
  }
  /** The bank approver makes the bank configurator's configuration set the version. */
  async approveConfiguration() {
    const maker = this.user;
    await this.as('example-bank', 'example.checker');
    await this.go('governance');
    await this.approveRow('config:example-bank');
    await this.as('example-bank', maker);
  }
  /**
   * A wait the viewer need not watch (a payment waiting for its scheme's answer): a card says what happens and for how long, and
   * the time is cut from the video. It starts once the scene's voice has finished, so no voice is cut.
   */
  cuts: { from: number; to: number }[] = [];
  async skip(title: string, fn: () => Promise<void>, minSeconds = 0) {
    const last = this.marks[this.marks.length - 1];
    if (last && !DRY) { const left = last.at + last.seconds + 0.3 - this.now(); if (left > 0) await this.page.waitForTimeout(left * 1000); }
    const t0 = this.now();
    await fn();
    const took = this.now() - t0;
    // A wait shorter than minSeconds is kept as it was, with no card.
    if (took < minSeconds) return;
    await this.page.evaluate(([t, u]: string[]) => (window as any).__card?.(t, u), [title, took < 60 ? 'A moment later' : `${Math.round(took / 60)} minute(s) later`]);
    await this.wait(2500);
    await this.page.evaluate(() => (window as any).__card?.());
    // Cut what came before the card, keep the card's last 2.5 seconds.
    if (!DRY && took > 5) this.cuts.push({ from: t0 + 0.5, to: t0 + took });
  }
  /** A time on the recorded clock, on the clock of the video once the cuts are made. */
  cutTime(at: number) {
    return at - this.cuts.filter((c) => c.to <= at).reduce((n, c) => n + (c.to - c.from), 0);
  }
  /** A terminal panel over the page, showing real command output. */
  async terminal(command: string, output: string) {
    await this.page.evaluate(([c, o]: string[]) => (window as any).__terminal?.(c, o), [command, output]);
  }
  async closeTerminal() {
    await this.page.evaluate(() => (window as any).__terminal?.());
  }
}

// The cursor, click ripple, highlight, captions, chapter cards and terminal, drawn into every page.
const OVERLAY = `
(() => {
  const css = document.createElement('style');
  css.textContent = \`
    #__cur { position: fixed; z-index: 2147483647; width: 22px; height: 22px; margin: -3px 0 0 -3px; pointer-events: none;
      background: no-repeat url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='22' height='22'><path d='M2 1 L2 18 L7 13.5 L10.5 21 L13 20 L9.6 12.6 L17 12.6 Z' fill='white' stroke='black' stroke-width='1.3'/></svg>"); }
    .__ripple { position: fixed; z-index: 2147483646; width: 38px; height: 38px; margin: -19px 0 0 -19px; border-radius: 50%;
      border: 3px solid #0f4c81; pointer-events: none; animation: __r .6s ease-out forwards; }
    @keyframes __r { from { transform: scale(.3); opacity: 1 } to { transform: scale(1.5); opacity: 0 } }
    #__mark { position: fixed; z-index: 2147483643; border: 3px solid #e8a33d; border-radius: 6px; pointer-events: none;
      box-shadow: 0 0 0 4000px rgba(10, 15, 20, .18); transition: all .25s ease; }
    #__cap { position: fixed; z-index: 2147483645; left: 50%; bottom: 16px; transform: translateX(-50%); max-width: 1060px; width: calc(100% - 80px);
      background: rgba(15, 20, 26, .88); color: #fff; font: 500 19px/1.4 system-ui, sans-serif; padding: 10px 18px; border-radius: 6px; pointer-events: none; text-align: center; }
    #__cap:empty { display: none; }
    #__card { pointer-events: none; position: fixed; inset: 0; z-index: 2147483644; background: #0f2d4f; border-top: 6px solid #5fd3c5; color: #fff; display: flex; flex-direction: column;
      align-items: center; justify-content: center; gap: 14px; font-family: system-ui, sans-serif; text-align: center; padding: 0 80px; }
    #__card h1 { font: 600 46px/1.15 'Segoe UI', 'Noto Sans', system-ui, sans-serif; margin: 0; letter-spacing: .01em; }
    #__card p { font: 400 23px/1.4 system-ui, sans-serif; margin: 0; color: #c9d9ec; max-width: 900px; }
    #__term { position: fixed; z-index: 2147483644; left: 40px; right: 40px; top: 60px; bottom: 110px; background: #0d1117; color: #d6e2ee;
      font: 13px/1.5 ui-monospace, Menlo, Consolas, monospace; padding: 16px 20px; border-radius: 8px; overflow: hidden; white-space: pre-wrap; box-shadow: 0 10px 40px rgba(0,0,0,.4); }
    #__term b { color: #5fd3c5; }
  \`;
  const add = () => {
    if (document.getElementById('__cur')) return;
    document.head.appendChild(css);
    const cur = document.createElement('div'); cur.id = '__cur'; cur.style.left = '-40px'; cur.style.top = '-40px'; document.body.appendChild(cur);
    const cap = document.createElement('div'); cap.id = '__cap'; document.body.appendChild(cap);
    addEventListener('mousemove', (e) => { cur.style.left = e.clientX + 'px'; cur.style.top = e.clientY + 'px'; }, true);
    addEventListener('mousedown', (e) => { const r = document.createElement('div'); r.className = '__ripple'; r.style.left = e.clientX + 'px'; r.style.top = e.clientY + 'px'; document.body.appendChild(r); setTimeout(() => r.remove(), 650); }, true);
    window.__caption = (t) => { cap.textContent = t || ''; };
    window.__mark = (r) => {
      let m = document.getElementById('__mark');
      if (!r) { m && m.remove(); return; }
      if (!m) { m = document.createElement('div'); m.id = '__mark'; document.body.appendChild(m); }
      m.style.left = (r.x - 5) + 'px'; m.style.top = (r.y - 5) + 'px'; m.style.width = (r.w + 10) + 'px'; m.style.height = (r.h + 10) + 'px';
    };
    window.__card = (t, s) => {
      let c = document.getElementById('__card');
      if (!t) { c && c.remove(); return; }
      if (!c) { c = document.createElement('div'); c.id = '__card'; document.body.appendChild(c); }
      c.innerHTML = '';
      const h = document.createElement('h1'); h.textContent = t; c.appendChild(h);
      if (s) { const p = document.createElement('p'); p.textContent = s; c.appendChild(p); }
    };
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

export interface Production {
  NAME: string;
  TITLE: string;
  PACE?: Pace;
  scenes: (d: Director) => Scene[];
  /** Before the first scene: open the first page. Default: the studio's catalogue. */
  open?: (d: Director) => Promise<void>;
}

/** Runs a production: recorded with voice, or, with TH_DEMO_DRY=1, as a fast check of every step. */
export async function record(prod: Production, outDir: string) {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), `th-demo-${prod.NAME}-`));
  const cache = path.join(outDir, '.voice');
  fs.mkdirSync(cache, { recursive: true });
  const d = new Director(process.env.TH_TTS, cache, prod.PACE ?? NORMAL);
  const list = prod.scenes(d);
  // Voice every line first, so that a failing voice stops the run before the recording.
  for (const s of list) d.voice(s.say);

  if (!fs.existsSync(path.join(STUDIO, 'web', 'dist', 'index.html'))) spawnSync('npm', ['run', 'web:build'], { cwd: STUDIO, stdio: 'inherit' });
  // A recording may show the live assistant; a dry run never calls a model.
  d.server = await startServer({}, { keepAiKeys: !DRY });
  const browser = await require('playwright').chromium.launch();
  const size = { width: 1280, height: 720 };
  // bypassCSP: a deployed studio's or console's content security policy would refuse the overlay's styles.
  const context = await browser.newContext({ viewport: size, bypassCSP: true, ...(DRY ? {} : { recordVideo: { dir: work, size } }), acceptDownloads: true });
  await context.addInitScript(OVERLAY);
  // The recordings show the studio without the assistant panel, unless a production opens it.
  await context.addInitScript(() => { try { if (localStorage.getItem('th.dock') === null) localStorage.setItem('th.dock', '0'); } catch { /* no storage */ } });
  const page = await context.newPage();
  d.start(page);
  await d.as('owner', 'shared.designer');
  let length = 0;
  try {
    if (prod.open) await prod.open(d);
    else await d.go('catalogue');
    for (const [i, s] of list.entries()) {
      process.stdout.write(`scene ${i + 1}/${list.length} at ${d.now().toFixed(1)}s\n`);
      // TH_DEMO_FAIL_SHOT=<file.png>: a screenshot of the page as it was when a scene failed.
      await d.scene(s).catch(async (e) => {
        if (process.env.TH_DEMO_FAIL_SHOT) await page.screenshot({ path: process.env.TH_DEMO_FAIL_SHOT }).catch(() => undefined);
        throw e;
      });
    }
    await d.wait(1500);
    length = d.now();
  } finally {
    await context.close();
    await browser.close();
    d.server.stop();
  }
  if (DRY) {
    console.log(`${prod.NAME}: ${list.length} scenes ran, dry`);
    fs.rmSync(work, { recursive: true, force: true });
    return { scenes: list.length };
  }
  const webm = fs.readdirSync(work).find((f) => f.endsWith('.webm'))!;
  const video = path.join(work, webm);

  // The waits a production skipped are cut from the video; every line's time moves with them.
  for (const m of d.marks) m.at = d.cutTime(m.at);
  length = d.cutTime(length);
  const keep: [number, number | undefined][] = [];
  let from = 0;
  for (const c of d.cuts) { keep.push([from, c.from]); from = c.to; }
  keep.push([from, undefined]);
  // A long video's progress lines overflow spawnSync's default buffer of 1 MB, which kills ffmpeg: keep its errors only.
  const ffmpeg = (args: string[]) => {
    const ff = spawnSync('ffmpeg', ['-nostats', '-loglevel', 'error', ...args], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
    if (ff.status !== 0) throw new Error(`ffmpeg failed (${ff.status ?? ff.signal}): ${ff.stderr.slice(-2000)}`);
  };
  // TH_DEMO_CRF sets the quality (default 26; a higher number is a smaller file).
  const encode = ['-c:v', 'libx264', '-preset', 'medium', '-crf', process.env.TH_DEMO_CRF ?? '26', '-pix_fmt', 'yuv420p'];
  // First the cuts, video only: each kept part read as an input of its own, seeking to its start. (Trimming one input in several
  // branches holds every frame of a long cut in memory, more than a long recording with a long wait cut from it fits.)
  let picture = video;
  if (d.cuts.length) {
    picture = path.join(work, 'cut.mp4');
    const graph = path.join(work, 'cut.txt');
    fs.writeFileSync(graph, [...keep.map((_, i) => `[${i}:v]setpts=PTS-STARTPTS[v${i}]`), `${keep.map((_, i) => `[v${i}]`).join('')}concat=n=${keep.length}:v=1:a=0[vout]`].join(';'));
    ffmpeg(['-y', ...keep.flatMap(([a, b]) => ['-ss', a.toFixed(3), ...(b === undefined ? [] : ['-to', b.toFixed(3)]), '-i', video]), '-filter_complex_script', graph, '-map', '[vout]', ...encode, picture]);
  }
  // Then the voice: each line at the time its scene started, mixed into one track, beside the picture.
  const out = path.join(outDir, `${prod.NAME}.mp4`);
  const voiced = d.marks.filter((m) => m.wav);
  const args = ['-y', '-i', picture];
  for (const m of voiced) args.push('-i', m.wav!);
  if (voiced.length) {
    const delays = voiced.map((m, i) => `[${i + 1}:a]adelay=${Math.round(m.at * 1000)}|${Math.round(m.at * 1000)}[a${i}]`);
    const graph = path.join(work, 'mix.txt');
    fs.writeFileSync(graph, [...delays, `${voiced.map((_, i) => `[a${i}]`).join('')}amix=inputs=${voiced.length}:normalize=0,apad[aout]`].join(';'));
    args.push('-filter_complex_script', graph, '-map', '0:v', '-map', '[aout]', '-shortest', '-c:a', 'aac', '-b:a', '128k');
  }
  args.push(...(d.cuts.length ? ['-c:v', 'copy'] : encode), '-movflags', '+faststart', '-metadata', `title=${prod.TITLE}`, out);
  ffmpeg(args);

  // Subtitles beside the video, from the same lines and times.
  const ts = (s: number) => new Date(Math.max(0, s) * 1000).toISOString().slice(11, 23).replace('.', ',');
  const srt = d.marks.map((m, i) => `${i + 1}\n${ts(m.at)} --> ${ts(m.at + m.seconds)}\n${m.text}\n`).join('\n');
  fs.writeFileSync(out.replace(/\.mp4$/, '.srt'), srt);
  console.log(`${out}: ${(fs.statSync(out).size / 1e6).toFixed(1)} MB, ${Math.floor(length / 60)} min ${Math.round(length % 60)} s, ${list.length} scenes`);
  // The raw recording and the mix script were only the way to the mp4.
  fs.rmSync(work, { recursive: true, force: true });
  return { scenes: list.length, out };
}
