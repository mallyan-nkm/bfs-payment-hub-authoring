import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

// The native assemblies a distribution carries (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3, spec R-09-120 to R-09-123).
// A scheme's or a bank's native activities (`implementation.binding: native`, or a script-behind the runtime also implements,
// `implementation.native: true`) are code in a code-behind project; the platform's own (`p`, `<cluster>.p`) are runtime
// software and stay in the runtime image. When the studio builds a distribution it takes each such scope's module build:
//
//   TH_NATIVE_MODULES=build    build it from the code-behind project at publish (`dotnet build`, deterministic), cached by the
//                              SHA-256 of the project's sources and the native contract version; TH_NATIVE_PROJECTS names the
//                              project of a scope (`uae.wps=<dir>,...`; by default runtime/src/TransactHub.Activities.Wps for WPS)
//   TH_NATIVE_MODULES=<dir>    take the build made ahead of time from the same project (`<dir>/<scope>/native-module.json` and its
//                              assemblies, as runtime/tools/native-module/build.sh writes it), for a studio with no .NET SDK
//   unset                      no build: a promotable distribution with such activities is refused; a test distribution
//                              records the scopes it does not carry (`native.notCarried`), and the runtime runs none of them
//
// Each assembly goes in the distribution as base64 text (the content branch holds text), under
// `activities/native/<scope>/<name>.dll.base64`, with the SHA-256 of its bytes in the bill of materials; the bill of materials is
// signed with the manifest. The runtime checks every hash and the native contract at install and again when it loads them.

export const NATIVE_FOLDER = 'activities/native';
const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const DEFAULT_PROJECTS: Record<string, string> = { 'uae.wps': 'runtime/src/TransactHub.Activities.Wps' };
const CONTRACT_PROJECT = 'runtime/src/TransactHub.Activities.Contract/TransactHub.Activities.Contract.csproj';
/** The native contract this studio builds against when the runtime's sources are not here (a prebuilt module says its own). */
export const NATIVE_CONTRACT = '1.0.0';

export interface NativeModuleBuild {
  scope: string;
  assembly: string;
  contract: string;
  project?: string;
  sourceSha256?: string;
  files: { name: string; content: Buffer; sha256: string }[];
  /** The folder it was read from (a module build folder, as TH_NATIVE_DEV_MODULES names one). */
  dir?: string;
}

const sha = (b: Buffer | string) => `sha256:${createHash('sha256').update(b).digest('hex')}`;

/** Does the runtime run this activity as native code-behind? */
export function isNative(doc: any): boolean {
  return doc?.kind === 'activity' && (doc.implementation?.binding === 'native' || doc.implementation?.native === true);
}

/** A scope whose native code is the runtime image's: the platform and the cluster platforms. */
export const isPlatformScope = (scope: string) => scope === 'p' || /^[a-z0-9-]+\.p$/.test(scope);

/** The project of a scope's code-behind, by TH_NATIVE_PROJECTS, else the factory's. */
export function nativeProject(scope: string): string | undefined {
  const named = Object.fromEntries((process.env.TH_NATIVE_PROJECTS ?? '').split(',').map((s) => s.trim()).filter(Boolean).map((s) => s.split('=').map((x) => x.trim()) as [string, string]));
  const p = named[scope] ?? DEFAULT_PROJECTS[scope];
  return p ? path.resolve(REPO, p) : undefined;
}

/** The native contract version, from the contract project's Version (R-09-123). */
export function contractVersion(): string {
  const f = path.join(REPO, CONTRACT_PROJECT);
  if (!fs.existsSync(f)) return NATIVE_CONTRACT;
  return /<Version>([^<]+)<\/Version>/.exec(fs.readFileSync(f, 'utf8'))?.[1] ?? NATIVE_CONTRACT;
}

/** The SHA-256 of a project's sources: every .cs and .csproj file, by relative path, outside bin and obj. */
export function sourceSha256(project: string): string {
  const files: string[] = [];
  const walk = (d: string) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
      if (e.isDirectory()) { if (e.name !== 'bin' && e.name !== 'obj') walk(path.join(d, e.name)); continue; }
      if (/\.(cs|csproj)$/.test(e.name)) files.push(path.join(d, e.name));
    }
  };
  walk(project);
  const h = createHash('sha256');
  for (const f of files) h.update(path.relative(project, f).replace(/\\/g, '/')).update('\0').update(fs.readFileSync(f)).update('\0');
  return `sha256:${h.digest('hex')}`;
}

/** A module build folder (native-module.json and its assemblies), as the runtime's native host also reads it. */
export function readModuleFolder(dir: string): NativeModuleBuild | undefined {
  const f = path.join(dir, 'native-module.json');
  if (!fs.existsSync(f)) return undefined;
  const m = JSON.parse(fs.readFileSync(f, 'utf8'));
  const files = (m.files ?? []).map((x: any) => {
    const content = fs.readFileSync(path.join(dir, x.name));
    if (sha(content) !== x.sha256) throw new Error(`${path.join(dir, x.name)} does not match its SHA-256 in ${f}`);
    return { name: x.name, content, sha256: x.sha256 };
  });
  return { scope: m.scope, assembly: m.assembly, contract: m.contract, project: m.project, sourceSha256: m.sourceSha256, files, dir };
}

function writeModuleFolder(dir: string, m: NativeModuleBuild) {
  fs.mkdirSync(dir, { recursive: true });
  for (const f of m.files) fs.writeFileSync(path.join(dir, f.name), f.content);
  const { files, ...rest } = m;
  fs.writeFileSync(path.join(dir, 'native-module.json'), JSON.stringify({ format: 'th-native-module/1', ...rest, files: files.map((f) => ({ name: f.name, sha256: f.sha256 })) }, null, 2) + '\n');
}

/** Waits for a folder lock (another process building the same module), then runs f. */
function locked<T>(lock: string, f: () => T): T {
  const until = Date.now() + 15 * 60_000;
  for (;;) {
    try { fs.mkdirSync(lock); break; } catch (e: any) {
      if (e.code !== 'EEXIST') throw e;
      if (Date.now() > until) throw new Error(`${lock} is held for more than 15 minutes`);
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500);
    }
  }
  try { return f(); } finally { fs.rmSync(lock, { recursive: true, force: true }); }
}

/** Builds a scope's module from its code-behind project (deterministic), or takes the build already cached for the same sources. */
export function buildModule(scope: string, project: string): NativeModuleBuild {
  const csproj = fs.readdirSync(project).find((f) => f.endsWith('.csproj'));
  if (!csproj) throw new Error(`${project} holds no .csproj`);
  const text = fs.readFileSync(path.join(project, csproj), 'utf8');
  const assembly = /<AssemblyName>([^<]+)<\/AssemblyName>/.exec(text)?.[1] ?? csproj.replace(/\.csproj$/, '');
  const contract = contractVersion();
  const source = sourceSha256(project);
  const key = createHash('sha256').update(`${source}|${contract}`).digest('hex').slice(0, 24);
  const root = process.env.TH_NATIVE_BUILD_CACHE ?? path.join(os.tmpdir(), 'th-native-modules');
  const dir = path.join(root, scope, key);
  const cached = readModuleFolder(dir);
  if (cached) return cached;
  fs.mkdirSync(path.dirname(dir), { recursive: true });
  return locked(`${dir}.lock`, () => {
    const again = readModuleFolder(dir);
    if (again) return again;
    const out = `${dir}.out`;
    fs.rmSync(out, { recursive: true, force: true });
    const b = spawnSync('dotnet', ['build', path.join(project, csproj), '-c', 'Release', '--nologo', '-v', 'q', `-p:OutDir=${out}${path.sep}`, '-p:Deterministic=true', '-p:ContinuousIntegrationBuild=true', '-nodeReuse:false', '-p:UseSharedCompilation=false'], {
      encoding: 'utf8', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', MSBUILDDISABLENODEREUSE: '1' }, maxBuffer: 64 * 1024 * 1024,
    });
    if (b.status !== 0) throw new Error(`The native code-behind of ${scope} does not build (${project}):\n${(b.stdout ?? '').slice(-4000)}\n${(b.stderr ?? '').slice(-2000)}`);
    const content = fs.readFileSync(path.join(out, `${assembly}.dll`));
    const m: NativeModuleBuild = { scope, assembly, contract, project: path.relative(REPO, project).replace(/\\/g, '/'), sourceSha256: source, files: [{ name: `${assembly}.dll`, content, sha256: sha(content) }] };
    writeModuleFolder(dir, m);
    fs.rmSync(out, { recursive: true, force: true });
    return { ...m, dir };
  });
}

/** A scope's module, from the source TH_NATIVE_MODULES names; undefined when none is configured or the scope has no project. */
export function moduleFor(scope: string): NativeModuleBuild | undefined {
  const how = process.env.TH_NATIVE_MODULES;
  if (!how) return undefined;
  if (how === 'build') {
    const project = nativeProject(scope);
    return project && fs.existsSync(project) ? buildModule(scope, project) : undefined;
  }
  return readModuleFolder(path.join(path.resolve(how), scope));
}

/**
 * The native section of a distribution's manifest and its files (R-09-120): per scheme or bank scope with native activities its
 * module, with the activities (`name@release`) it must serve. A promotable distribution without a scope's module is refused; a
 * test distribution lists it as not carried.
 */
export function nativeModules(docs: any[], scopeOf: (name: string) => string, promotable: boolean): { section: any; files: Record<string, string>; artefacts: any[]; problems: string[] } {
  const byScope = new Map<string, string[]>();
  for (const d of docs.filter(isNative)) {
    const s = scopeOf(d.name);
    if (isPlatformScope(s)) continue;
    byScope.set(s, [...(byScope.get(s) ?? []), `${d.name}@${d.release}`].sort());
  }
  const modules: any[] = [];
  const files: Record<string, string> = {};
  const artefacts: any[] = [];
  const notCarried: string[] = [];
  const problems: string[] = [];
  let contract = contractVersion();
  for (const [scope, activities] of [...byScope].sort(([a], [b]) => a.localeCompare(b))) {
    const m = moduleFor(scope);
    if (!m) {
      if (promotable) problems.push(`${scope} has native activities (${activities.join(', ')}) and the studio has no build of their code-behind: set TH_NATIVE_MODULES (R-09-120)`);
      else notCarried.push(scope);
      continue;
    }
    if (m.scope !== scope) problems.push(`The module build for ${scope} is of ${m.scope}`);
    contract = m.contract ?? contract;
    const listed = m.files.map((f) => {
      const p = `${NATIVE_FOLDER}/${scope}/${f.name}.base64`;
      files[p] = `${f.content.toString('base64')}\n`;
      artefacts.push({ scope, name: f.name, kind: 'native-assembly', path: p, sha256: sha(files[p]), assemblySha256: f.sha256 });
      return { path: p, name: f.name, sha256: f.sha256 };
    });
    modules.push({ scope, assembly: m.assembly, ...(m.project ? { project: m.project } : {}), ...(m.sourceSha256 ? { sourceSha256: m.sourceSha256 } : {}), activities, files: listed });
  }
  const section = { contract, platform: 'runtime-image', modules, ...(notCarried.length ? { notCarried } : {}) };
  return { section, files, artefacts, problems };
}
