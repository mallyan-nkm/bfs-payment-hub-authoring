import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isSecretRef, resolveSecretSync } from '../secrets/secrets.js';
import { assemblyReferences } from './assembly-refs.js';
import { parseTrustedSigners, SignatureRejected, verifyFiles } from './signing.js';

// The native assemblies a distribution carries (docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3, spec R-09-120 to R-09-127).
// The native activities of every pack (`implementation.binding: native`, or a script-behind the runtime also implements,
// `implementation.native: true`) are code in a code-behind project: the platform's (`p`, R-09-126), a scheme's and a bank's. The
// runtime image holds none of them. When the studio builds a distribution it takes each such scope's module build:
//
//   TH_NATIVE_MODULES=build    build it from the code-behind project at publish (`dotnet build`, deterministic), cached by the
//                              SHA-256 of the project's sources and the native contract version; TH_NATIVE_PROJECTS names the
//                              project of a scope (`uae.wps=<dir>,...`; by default runtime/src/TransactHub.Activities.Wps for WPS)
//   TH_NATIVE_MODULES=<dir>    take the build made ahead of time from the same project (`<dir>/<scope>/native-module.json` and its
//                              assemblies, as runtime/tools/native-module/build.sh writes it), for a studio with no .NET SDK. The
//                              folder MUST be signed by a key TH_NATIVE_MODULE_SIGNERS trusts (R-09-127, release.json and
//                              release.sig as a distribution's are); an unsigned or wrongly signed folder is refused, unless
//                              TH_NATIVE_MODULES_UNVERIFIED is `dev` or `test` and NODE_ENV is not production, when it is taken
//                              and the distribution's manifest says so (`signature: { unverified: <mode> }`)
//   unset                      no build: the distribution records the scopes it does not carry (`native.notCarried`), and the
//                              runtime runs none of their activities natively; each runs its script-behind instead. An activity
//                              with no script-behind (`implementation.binding: native`) cannot run without its build, so a
//                              promotable distribution that needs one and has no build is refused
//
// Each assembly goes in the distribution as base64 text (the content branch holds text), under
// `activities/native/<scope>/<name>.dll.base64`, with the SHA-256 of its bytes in the bill of materials; the bill of materials is
// signed with the manifest. The runtime checks every hash and the native contract at install and again when it loads them. A
// module's assemblies reference the native contract and no other assembly of TransactHub (R-09-125): the studio refuses to carry one
// that does (package.NativeImageReference), and the runtime refuses such a distribution.

export const NATIVE_FOLDER = 'activities/native';
/** The core-banking port: the runtime calls it only through the gate the platform module contributes (R-09-279). */
export const CORE_BANKING_PORT = 'p.port.core-banking.v1';
const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const DEFAULT_PROJECTS: Record<string, string> = { p: 'runtime/src/TransactHub.Activities.Platform.Native', 'uae.wps': 'runtime/src/TransactHub.Activities.Wps' };
const CONTRACT_PROJECT = 'runtime/src/TransactHub.Activities.Contract/TransactHub.Activities.Contract.csproj';
/**
 * The native contract this studio builds against when the runtime's sources are not here (a prebuilt module says its own). 2.0.0
 * (R-09-261 to R-09-264): bound ports, no Service<T>; a module built against 1.x is refused here and on the runtime. 2.1.0 (R-09-276), a
 * minor: a balance enquiry on core banking, a body of bytes and a file put on the bound ports; a module built against 2.0 is taken.
 */
export const NATIVE_CONTRACT = '2.1.0';
/** The error dictionary's code of a module built against a native contract the runtime does not take (R-09-262). */
export const CONTRACT_UNSUPPORTED = 'native.ContractUnsupported';
/** The native contract's assembly: the one assembly of TransactHub a module references (R-09-125). */
export const CONTRACT_ASSEMBLY = 'TransactHub.Activities.Contract';
/** The error dictionary's code of a module that references the runtime image beyond the contract (R-09-125, Issue I-220). */
export const IMAGE_REFERENCE = 'package.NativeImageReference';
/** The signers of module builds made ahead of time that the studio trusts: the trusted-signers JSON, or a reference to it (R-09-127). */
export const MODULE_SIGNERS_ENV = 'TH_NATIVE_MODULE_SIGNERS';
/** `dev` or `test`: a module build made ahead of time is taken unsigned or wrongly signed, never when NODE_ENV is production. */
export const UNVERIFIED_ENV = 'TH_NATIVE_MODULES_UNVERIFIED';

export interface NativeModuleBuild {
  scope: string;
  assembly: string;
  contract: string;
  project?: string;
  sourceSha256?: string;
  files: { name: string; content: Buffer; sha256: string }[];
  /** The folder it was read from (a module build folder, as TH_NATIVE_DEV_MODULES names one). */
  dir?: string;
  /** A build made ahead of time: the key that signed it, or the mode that took it unverified (R-09-127). */
  signature?: { keyId: string } | { unverified: string; reason: string };
}

const sha = (b: Buffer | string) => `sha256:${createHash('sha256').update(b).digest('hex')}`;

/**
 * Does the runtime run this activity as native code-behind? A builtin a module answers on the runtime says so too (`native: true`, lint
 * L40): since native contract 2.1.0 the image answers no core-banking activity (R-09-278, R-09-282, Issues I-381, I-382), so a
 * distribution that carries one needs its scope's module, as it needs the module of an activity with no script-behind.
 */
export function isNative(doc: any): boolean {
  return doc?.kind === 'activity' && (doc.implementation?.binding === 'native' || doc.implementation?.native === true);
}

/** Whether a module built against `built` runs on a runtime of contract `own`: the same major, at a minor no later (R-09-123, R-09-262). */
export function contractSupported(built: string | undefined, own = contractVersion()): boolean {
  const [a, b] = [built, own].map((v) => /^(\d+)\.(\d+)\.(\d+)$/.exec(v ?? '')?.slice(1).map(Number));
  return !!a && !!b && a[0] === b[0] && a[1] <= b[1];
}

/** Orders two contract versions (major.minor.patch). */
function compareContract(a: string, b: string): number {
  const [x, y] = [a, b].map((v) => v.split('.').map(Number));
  for (let i = 0; i < 3; i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) - (y[i] ?? 0);
  return 0;
}

/** A platform scope: the platform and the cluster platforms. Since R-09-126 their native code is a module like any other. */
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

/**
 * The SHA-256 of a project's sources: of the lines `<sha256 hex>  <relative path>`, one per .cs and .csproj file outside bin and obj,
 * in the byte order of their paths, as `sha256sum` prints them. runtime/tools/native-module/build.sh computes the same with
 * `find`, `sort` and `sha256sum`, so a build made ahead of time names the sources it was built from (R-09-127).
 */
export function sourceSha256(project: string): string {
  const files: string[] = [];
  const walk = (d: string) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (e.isDirectory()) { if (e.name !== 'bin' && e.name !== 'obj') walk(path.join(d, e.name)); continue; }
      if (/\.(cs|csproj)$/.test(e.name)) files.push(path.relative(project, path.join(d, e.name)).replace(/\\/g, '/'));
    }
  };
  walk(project);
  files.sort((x, y) => Buffer.compare(Buffer.from(x), Buffer.from(y)));
  const lines = files.map((f) => `${createHash('sha256').update(fs.readFileSync(path.join(project, f))).digest('hex')}  ${f}\n`).join('');
  return `sha256:${createHash('sha256').update(lines).digest('hex')}`;
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
export function moduleFor(scope: string, env = process.env): NativeModuleBuild | undefined {
  const how = env.TH_NATIVE_MODULES;
  if (!how) return undefined;
  if (how === 'build') {
    const project = nativeProject(scope);
    return project && fs.existsSync(project) ? buildModule(scope, project) : undefined;
  }
  const dir = path.join(path.resolve(how), scope);
  const m = readModuleFolder(dir);
  return m ? { ...m, signature: moduleSignature(dir, scope, env) } : undefined;
}

/** The signers of module builds this studio trusts (TH_NATIVE_MODULE_SIGNERS: the JSON, or a reference to it). */
function moduleSigners(env: NodeJS.ProcessEnv) {
  const v = env[MODULE_SIGNERS_ENV];
  if (!v) throw new SignatureRejected(`${MODULE_SIGNERS_ENV} is not set: no signer of module builds is trusted`);
  return parseTrustedSigners(v.trimStart().startsWith('{') ? v : isSecretRef(v) ? resolveSecretSync(v, env).reveal() : v);
}

/**
 * The signature of a module build made ahead of time (R-09-127): `release.json` and `release.sig` in its folder, a `native-module`
 * statement for `native-module/<scope>` naming every other file of the folder with its SHA-256, by a key TH_NATIVE_MODULE_SIGNERS
 * trusts. An unsigned or wrongly signed folder is refused, unless TH_NATIVE_MODULES_UNVERIFIED says `dev` or `test` outside production.
 */
export function moduleSignature(dir: string, scope: string, env = process.env): { keyId: string } | { unverified: string; reason: string } {
  const files: Record<string, Buffer> = {};
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) if (e.isFile()) files[e.name] = fs.readFileSync(path.join(dir, e.name));
  try {
    const v = verifyFiles(files, { releaseId: `native-module/${scope}`, tenant: null, kind: 'native-module' }, moduleSigners(env));
    return { keyId: v.keyId };
  } catch (e) {
    if (!(e instanceof SignatureRejected)) throw e;
    const mode = env[UNVERIFIED_ENV];
    if ((mode === 'dev' || mode === 'test') && env.NODE_ENV !== 'production') return { unverified: mode, reason: e.message };
    const why = mode && env.NODE_ENV === 'production' ? ` (${UNVERIFIED_ENV}=${mode} is refused when NODE_ENV is production)` : '';
    throw new SignatureRejected(`The module build of ${scope} in ${dir} is refused: ${e.message}${why}. A build made ahead of time is signed by runtime/tools/native-module/build.sh with a key ${MODULE_SIGNERS_ENV} trusts (R-09-127)`);
  }
}

/**
 * What a module's assemblies reference of TransactHub beyond the native contract and the module's own assemblies (R-09-125): each
 * a refusal, with the error dictionary's code. An assembly that is not a .NET assembly, or that does not reference the contract, is
 * refused too.
 */
export function imageReferences(m: NativeModuleBuild): string[] {
  const own = new Set(m.files.map((f) => f.name.replace(/\.dll$/i, '').toLowerCase()));
  const out: string[] = [];
  for (const f of m.files) {
    const refs = assemblyReferences(f.content);
    if (!refs) { out.push(`${f.name} of ${m.scope} is not a .NET assembly`); continue; }
    if (f.name.replace(/\.dll$/i, '') === m.assembly && !refs.some((r) => r.name === CONTRACT_ASSEMBLY)) out.push(`${f.name} of ${m.scope} does not reference the native contract (${CONTRACT_ASSEMBLY})`);
    for (const r of refs)
      if (/^TransactHub\./i.test(r.name) && r.name !== CONTRACT_ASSEMBLY && !own.has(r.name.toLowerCase()))
        out.push(`${IMAGE_REFERENCE}: ${f.name} of ${m.scope} references ${r.name}, which is the runtime image's; a native module references ${CONTRACT_ASSEMBLY} and no other assembly of TransactHub (R-09-125)`);
  }
  return out;
}

/**
 * The native section of a distribution's manifest and its files (R-09-120, R-09-126): per scope with native activities (the platform's too) its
 * module, with the activities (`name@release`) it must serve. A promotable distribution without the module of an activity that has
 * no script-behind (`binding: native`) is refused; a
 * test distribution lists it as not carried.
 */
export function nativeModules(docs: any[], scopeOf: (name: string) => string, promotable: boolean): { section: any; files: Record<string, string>; artefacts: any[]; problems: string[] } {
  const byScope = new Map<string, string[]>();
  const nativeOnly = new Map<string, string[]>();
  for (const d of docs.filter(isNative)) {
    const s = scopeOf(d.name);
    byScope.set(s, [...(byScope.get(s) ?? []), `${d.name}@${d.release}`].sort());
    if (d.implementation?.binding !== 'script') nativeOnly.set(s, [...(nativeOnly.get(s) ?? []), `${d.name}@${d.release}`].sort());
  }
  const modules: any[] = [];
  const files: Record<string, string> = {};
  const artefacts: any[] = [];
  const notCarried: string[] = [];
  const problems: string[] = [];
  // The manifest names the latest minor its modules were built against (R-09-276): a 2.0 module beside a 2.1 one runs on a 2.1 runtime.
  let contract: string | undefined;
  for (const [scope, activities] of [...byScope].sort(([a], [b]) => a.localeCompare(b))) {
    let m: NativeModuleBuild | undefined;
    try { m = moduleFor(scope); } catch (e: any) { problems.push(e.message); continue; }
    if (!m) {
      const only = nativeOnly.get(scope) ?? [];
      if (promotable && only.length) problems.push(`${scope} has native activities with no script-behind (${only.join(', ')}) and the studio has no build of their code-behind: set TH_NATIVE_MODULES (R-09-120)`);
      else notCarried.push(scope);
      continue;
    }
    if (m.scope !== scope) problems.push(`The module build for ${scope} is of ${m.scope}`);
    if (!contractSupported(m.contract)) {
      problems.push(`${CONTRACT_UNSUPPORTED}: the module build of ${scope} was built against native contract ${m.contract ?? '(none)'}; this studio builds distributions for native contract ${contractVersion()}: rebuild it against ${contractVersion()} (runtime/tools/native-module/build.sh, docs/runtime/PACKAGING-AND-DEPLOYMENT.md section 3.2)`);
      continue;
    }
    const refused = imageReferences(m);
    if (refused.length) { problems.push(...refused); continue; }
    if (m.contract && (!contract || compareContract(m.contract, contract) > 0)) contract = m.contract;
    const listed = m.files.map((f) => {
      const p = `${NATIVE_FOLDER}/${scope}/${f.name}.base64`;
      files[p] = `${f.content.toString('base64')}\n`;
      artefacts.push({ scope, name: f.name, kind: 'native-assembly', path: p, sha256: sha(files[p]), assemblySha256: f.sha256 });
      return { path: p, name: f.name, sha256: f.sha256 };
    });
    modules.push({ scope, assembly: m.assembly, ...(m.project ? { project: m.project } : {}), ...(m.sourceSha256 ? { sourceSha256: m.sourceSha256 } : {}), ...(m.signature ? { signature: m.signature } : {}), activities, files: listed });
  }
  // A module whose activities call core banking needs the platform module beside it: the image calls core banking only through the
  // gate a module contributed, and with none it refuses every call (R-09-279, R-09-295, Issue I-386).
  if (promotable && !modules.some((m) => m.scope === 'p')) {
    const byKey = new Map<string, any>(docs.map((d) => [`${d.name}@${d.release}`, d]));
    const banking = modules.filter((m) => m.activities.some((a: string) => (byKey.get(a)?.ports ?? []).includes(CORE_BANKING_PORT)));
    for (const m of banking) problems.push(`The module of ${m.scope} carries activities that name ${CORE_BANKING_PORT} (${m.activities.filter((a: string) => (byKey.get(a)?.ports ?? []).includes(CORE_BANKING_PORT)).join(', ')}), and the distribution carries no platform module: the runtime calls core banking only through the gate the platform module contributes (R-09-279). Set TH_NATIVE_MODULES so the platform module is built too`);
  }
  const section = { contract: contract ?? contractVersion(), modules, ...(notCarried.length ? { notCarried } : {}) };
  return { section, files, artefacts, problems };
}
