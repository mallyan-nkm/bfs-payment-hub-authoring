import { createHash } from 'node:crypto';
import { canonicalJson, clone } from '../core/canonical.js';
import { parseYaml, toYaml } from '../core/yaml.js';
import { expandWorkflow } from '../core/expand.js';
import { OWNER_TENANT, StoreError, type Entry } from '../core/store.js';
import { portBindingProblems, SLOT_WRITE_OPEN, withinBounds } from '../checks/checks.js';
import { packEntries } from '../governance/packs.js';
import { exportPackage, signPackage, type Target } from './exporter.js';
import { bindingOf } from './bindings.js';
import { signFiles, type ReleaseClaims } from './signing.js';
import { resolveReferenceValues } from '../core/reference.js';
import { compareReleases } from '../core/names.js';
import type { StudioEnv } from '../env.js';
import { scopedEnv } from '../core/scope.js';
import { nativeModules } from './native-modules.js';

// A bank's distribution and configuration release (docs/runtime/PACKAGING-AND-DEPLOYMENT.md, spec R-09-48 to R-09-52,
// R-11-37 to R-11-40). A distribution is the complete, signed copy of everything one bank runs on one engine: the platform
// packs, the scheme packs the bank licenses, the bank's own pack and the engine binding, resolved at build time so that
// the runtime never combines versions. Its manifest carries the bill of materials (every pack release and every artefact
// release with its SHA-256), the parameter contract (every dial, its type and bounds), the slot contracts its schemes fill
// and the longest wait it declares. A configuration release carries the bank's values (dials with validFrom), its port
// bindings (secretRef only) and calendars, and the parameter contracts it was checked against. Both are signed with the
// statement of R-09-23 (kind `distribution` or `configuration`) and published to the content branch with a tag.

export const DIST_ROOT = 'releases/distributions';
export const CONFIG_ROOT = 'releases/configuration';
export const distTag = (bank: string, id: string) => `dist/${bank}/${id}`;
export const configTag = (bank: string, release: string) => `config/${bank}/${release}`;
export const distFolder = (bank: string, id: string) => `${DIST_ROOT}/${bank}/${id}`;
export const configFolder = (bank: string, release: string) => `${CONFIG_ROOT}/${bank}/${release}`;
export const DIST_ID = /^\d{4}\.\d{2}\.\d{2}-[1-9]\d*$/;

const sha = (t: string | Buffer) => `sha256:${createHash('sha256').update(t).digest('hex')}`;
const scopeOfName = (n: string) => (n.split('.')[0] === 'p' || n.split('.')[0] === 'b' ? n.split('.')[0] : n.split('.').slice(0, 2).join('.'));
const majorOf = (name: string) => Number(/\.v(\d+)$/.exec(name)?.[1] ?? 1);

export class DistributionRefused extends StoreError {
  constructor(message: string) {
    super('distribution', message);
  }
}

/** The bank's pack manifest (its lock file), by tenant. */
export function bankPackOf(env: StudioEnv, bank: string): Entry | undefined {
  return env.store.byKind('pack').find((e) => e.doc.scope === 'b' && (e.doc.tenant ?? 'example-bank') === bank);
}

/** The banks the studio knows: every tenant with a bank pack. */
export function banks(env: StudioEnv): string[] {
  return [...new Set(env.store.byKind('pack').filter((e) => e.doc.scope === 'b').map((e) => e.doc.tenant ?? 'example-bank'))].sort();
}

/**
 * The schemes a bank licenses (R-11-37): the bank pack's `licensedSchemes`, each a scheme of the factory. A lock file without
 * the list is refused (Issue I-111): a distribution carries the schemes the bank licenses and no other.
 */
export function licensedSchemes(env: StudioEnv, bank: string): string[] {
  const pack = bankPackOf(env, bank)?.doc;
  if (!pack) throw new DistributionRefused(`${bank} has no bank pack`);
  // Required since Issue I-111: every bank's lock file names them (`npm run studio -- bank-pack <bank> --licensed <scheme>`).
  if (!Array.isArray(pack.licensedSchemes) || !pack.licensedSchemes.length) throw new DistributionRefused(`The bank pack of ${bank} names no licensedSchemes: a distribution carries the schemes the bank licenses and no other (R-11-37)`);
  const unknown = pack.licensedSchemes.filter((s: string) => !env.store.byKind('scheme').some((e) => e.doc.scope === s));
  if (unknown.length) throw new DistributionRefused(`The bank pack of ${bank} licenses ${unknown.join(', ')}, which is no scheme of the factory`);
  return [...pack.licensedSchemes].sort();
}

/** The scopes a distribution's platform is made of: `p` and the cluster platform of each licensed scheme. */
function platformScopes(schemes: string[]): string[] {
  return ['p', ...new Set(schemes.map((s) => `${s.split('.')[0]}.p`))];
}

/** Published entries of the latest release of each artefact of a scope (a bank's own for `b`). */
function entriesOf(env: StudioEnv, scope: string, bank?: string): Entry[] {
  return packEntries(env, scope, scope === 'b' ? bank : undefined).filter((e) => e.kind !== 'simulator' && e.kind !== 'test');
}

/**
 * The slot contracts the platform offers (R-11-38): each slot of a platform workflow is the contract `<slot>@<major>`, where
 * the major is the platform workflow's (its interface changes only with a new major). A leg declares the contract it fills
 * with `implements: { slot, of }`.
 */
export function slotContracts(platform: any[]): { id: string; workflow: string; slot: string; contract: any; sha256: string }[] {
  const out: any[] = [];
  for (const wf of platform.filter((d) => d.kind === 'workflow' && d.wfKind === 'platform'))
    for (const s of wf.slots ?? []) out.push({ id: `${s.name}@${majorOf(wf.name)}`, workflow: wf.name, slot: s.name, contract: s.contract, sha256: sha(canonicalJson(s.contract ?? {})) });
  return out.sort((a, b) => `${a.workflow}#${a.slot}`.localeCompare(`${b.workflow}#${b.slot}`));
}

const within = (paths: string[] | undefined, allowed: string[], own: string) =>
  (paths ?? []).filter((p) => !allowed.some((a) => p === a || p.startsWith(`${a}.`) || p.startsWith(`${a}[`)) && !p.startsWith(`ext.${own}`));

/**
 * The slot-contract check (R-11-38, lint L43): every leg of a licensed scheme fills a slot of a platform workflow of the
 * distribution, with that slot's contract: the workflow it names is in the distribution at the major it names, has the slot,
 * and the leg's outcomes, reads and writes (beyond its scheme's own extensions) are within the slot's (Issue I-112; a write the
 * owner has still to decide is let through and listed, I-141). Returns what each scheme fills, or the mismatches.
 */
export function checkSlotContracts(platform: any[], schemes: { scope: string; docs: any[] }[]): { fills: any[]; problems: string[] } {
  const contracts = slotContracts(platform);
  const fills: any[] = [];
  const problems: string[] = [];
  for (const s of schemes) {
    for (const leg of s.docs.filter((d) => d.kind === 'workflow' && d.wfKind === 'leg')) {
      const slot = leg.implements?.slot;
      for (const of of leg.implements?.of ?? []) {
        const c = contracts.find((x) => x.workflow === of && x.slot === slot);
        if (!c) {
          const other = contracts.find((x) => x.workflow.replace(/\.v\d+$/, '') === of.replace(/\.v\d+$/, '') && x.slot === slot);
          problems.push(other ? `${leg.name} of ${s.scope} fills ${slot}@${majorOf(of)} of ${of}, but the platform offers ${other.id} (${other.workflow})` : `${leg.name} of ${s.scope} fills ${slot} of ${of}, which the platform of this distribution does not offer`);
          continue;
        }
        const bad = [
          ...(leg.exit?.outcomes ?? []).filter((o: string) => !(c.contract?.outcomes ?? []).includes(o)).map((o: string) => `outcome ${o}`),
          ...within(leg.entry?.reads, c.contract?.reads ?? [], s.scope).map((p) => `read ${p}`),
          ...within(leg.exit?.writes, c.contract?.writes ?? [], s.scope).filter((p) => !SLOT_WRITE_OPEN[`${leg.name} ${of} ${p}`]).map((p) => `write ${p}`),
        ];
        if (bad.length) problems.push(`${leg.name} of ${s.scope} does not fit the slot contract ${c.id} of ${of}: ${bad.join(', ')}`);
        else fills.push({ scheme: s.scope, leg: leg.name, release: leg.release, contract: c.id, workflow: of, sha256: c.sha256 });
      }
    }
  }
  return { fills: fills.sort((a, b) => `${a.workflow}${a.leg}`.localeCompare(`${b.workflow}${b.leg}`)), problems };
}

/**
 * The parameter contract of a set of artefacts (R-11-39, lint L44): every dial, as `<artefact>#<parameter>`, with its type,
 * default and bounds. A dial whose default lies outside its own bounds, or whose bounds cross, is a problem.
 */
export function parameterContract(docs: any[]): { contract: { format: string; parameters: any[] }; problems: string[] } {
  const parameters: any[] = [];
  const problems: string[] = [];
  for (const d of docs) {
    for (const [k, decl] of Object.entries<any>(d.parameters ?? {})) {
      const name = `${d.name}#${k}`;
      if (!decl?.type) problems.push(`${name} declares no type`);
      if (decl?.default === undefined) problems.push(`${name} declares no default`);
      else if (!withinBounds(decl, decl.default)) problems.push(`${name}: its default ${JSON.stringify(decl.default)} is outside its own bounds ${JSON.stringify(decl.bounds)}`);
      parameters.push({ parameter: name, owner: d.name, release: d.release, type: decl?.type, default: decl?.default, ...(decl?.bounds ? { bounds: decl.bounds } : {}), ...(decl?.tenantSettable === false ? { tenantSettable: false } : {}) });
    }
  }
  return { contract: { format: 'th-parameter-contract/1', parameters: parameters.sort((a, b) => a.parameter.localeCompare(b.parameter)) }, problems };
}

const SECONDS: Record<string, number> = { D: 86400, H: 3600, M: 60, S: 1 };
export function isoSeconds(d: string | undefined): number | undefined {
  const m = /^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$/.exec(String(d ?? ''));
  if (!m) return undefined;
  return (Number(m[1] ?? 0) * SECONDS.D) + Number(m[2] ?? 0) * SECONDS.H + Number(m[3] ?? 0) * SECONDS.M + Number(m[4] ?? 0);
}

/** The longest wait a distribution declares (R-09-56): the largest bound or default of a duration dial that is a wait or a timeout. */
export function longestWait(contract: { parameters: any[] }): string {
  let best = 0;
  for (const p of contract.parameters) {
    if (p.type !== 'duration' || !/wait|timeout|hold/i.test(p.parameter.split('#')[1])) continue;
    const s = Math.max(isoSeconds(p.bounds?.max) ?? 0, isoSeconds(p.default) ?? 0);
    best = Math.max(best, s);
  }
  const days = Math.floor(best / 86400);
  const rest = best % 86400;
  return best === 0 ? 'PT0S' : `P${days ? `${days}D` : ''}${rest ? `T${rest}S` : ''}`;
}

/** The next distribution id of a bank on a date: `YYYY.MM.DD-n`, after every id its folders and tags hold. */
export function nextDistributionId(env: StudioEnv, bank: string, date = new Date().toISOString().slice(0, 10)): string {
  const day = date.replace(/-/g, '.');
  const used = new Set<number>();
  const prefix = `${distFolder(bank, day)}-`;
  for (const p of env.store.content?.files.keys() ?? []) if (p.startsWith(prefix)) used.add(Number(p.slice(prefix.length).split('/')[0]));
  for (const p of (env.store.extra.distributions ?? []) as string[]) if (p.startsWith(`${bank}/${day}-`)) used.add(Number(p.split('-').pop()));
  let n = 1;
  while (used.has(n)) n++;
  return `${day}-${n}`;
}

export interface BuiltBundle {
  kind: 'distribution' | 'configuration';
  bank: string;
  id: string;
  releaseId: string;
  folder: string;
  tag: { name: string; message: string };
  files: Record<string, string>;
  manifest: any;
}

/**
 * Builds a bank's distribution for one engine from the published releases (R-09-48, R-11-37 to R-11-39): the platform, the
 * schemes the bank licenses, its bank pack and the engine binding. Refused when a scheme does not fit its platform's slot
 * contract, when the parameter contract has a problem, or when a licensed scheme is not in the studio.
 */
export function buildDistribution(env: StudioEnv, req: { bank: string; engine: Target; id?: string; changeSets?: string[]; date?: string; withSimulators?: boolean }): BuiltBundle {
  const { bank, engine } = req;
  // A bank's distribution holds shared content and that bank's own only (R-11-48).
  env = scopedEnv(env, bank);
  const schemes = licensedSchemes(env, bank);
  const schemeDocs = env.store.byKind('scheme').map((e) => e.doc);
  for (const s of schemes) if (!schemeDocs.some((d) => d.scope === s)) throw new DistributionRefused(`${bank} licenses ${s}, which the studio has no scheme pack for`);
  const scopes = [...platformScopes(schemes), ...schemes, 'b'];
  const byScope = new Map(scopes.map((s) => [s, entriesOf(env, s, bank)] as const));
  const platformDocs = [...(byScope.get('p') ?? []), ...platformScopes(schemes).slice(1).flatMap((s) => byScope.get(s) ?? [])].map((e) => e.doc);
  const slot = checkSlotContracts(platformDocs, schemes.map((s) => ({ scope: s, docs: (byScope.get(s) ?? []).map((e) => e.doc) })));
  if (slot.problems.length) throw new DistributionRefused(`The distribution of ${bank} is refused: ${slot.problems.join('; ')}`);
  // Reference tables are values: they travel in the configuration release, never in a distribution (R-01-25).
  const all = [...byScope.values()].flat().filter((e) => e.kind !== 'reference-table');
  const params = parameterContract(all.map((e) => e.doc));
  if (params.problems.length) throw new DistributionRefused(`The parameter contract of ${bank}'s distribution has problems: ${params.problems.join('; ')}`);
  // The engine binding at its exact release, pinned in the bill of materials (Issue I-83).
  const binding = bindingOf(env, engine);
  if (!binding) throw new DistributionRefused(`No engine binding for ${engine}`);

  const files: Record<string, string> = {};
  const artefacts: any[] = [];
  const dirOf: Record<string, string> = { activity: 'activities', script: 'activities', rule: 'rules', ruleset: 'rules', table: 'rules', port: 'ports', extension: 'extensions', hook: 'hooks', 'error-dictionary': 'errors', 'operations-view': 'ops', scheme: 'schemes', workflow: 'workflows/authored', sample: 'samples' };
  for (const e of all.sort((a, b) => a.name.localeCompare(b.name))) {
    const dir = dirOf[e.kind] ?? e.kind;
    const p = `${dir}/${e.kind === 'scheme' ? `${e.doc.scope}.scheme` : e.name}@${e.release}.yaml`;
    files[p] = toYaml(e.doc);
    artefacts.push({ scope: scopeOfName(e.kind === 'scheme' ? `${e.doc.scope}.x` : e.name), name: e.kind === 'scheme' ? `${e.doc.scope}.scheme` : e.name, kind: e.kind, release: e.release, hash: e.hash, path: p, sha256: sha(files[p]) });
  }
  files['binding.yaml'] = toYaml(binding.doc);
  artefacts.push({ scope: 'p', name: binding.name, kind: 'engine-binding', release: binding.release, hash: binding.hash, path: 'binding.yaml', sha256: sha(files['binding.yaml']) });
  // The tests the release passed: the test documents of its packs (R-09-48); their results are the release gate's.
  for (const s of scopes) for (const t of packEntries(env, s, s === 'b' ? bank : undefined).filter((e) => e.kind === 'test')) files[`tests/${t.name}.yaml`] = toYaml(t.doc);

  // Every platform workflow in its expanded form, with the legs of the licensed schemes in its slots, and the engine's own
  // form: the signed engine package of each, for this engine (R-09-17).
  const platformWfs = platformDocs.filter((d) => d.kind === 'workflow' && d.wfKind === 'platform').sort((a, b) => a.name.localeCompare(b.name));
  const slots: any[] = [];
  for (const wf of platformWfs) {
    files[`workflows/${wf.name}@${wf.release}.yaml`] = toYaml(expandWorkflow(clone(wf), (x) => env.store.doc(x)));
    for (const s of wf.slots ?? []) slots.push({ workflow: wf.name, slot: s.name, contract: `${s.name}@${majorOf(wf.name)}`, legs: slot.fills.filter((f) => f.workflow === wf.name && (byScope.get(f.scheme) ?? []).some((e) => e.name === f.leg && e.doc.implements?.slot === s.name)).map((f) => `${f.leg}@${f.release}`) });
    const legs = slots.filter((x) => x.workflow === wf.name).flatMap((x) => x.legs);
    if (!legs.length) continue;
    const pkg = exportPackage(env, { workflows: [wf.name], target: engine, tenant: bank, schemes, values: false, withSimulators: req.withSimulators });
    const signed = signPackage(pkg, req.changeSets?.[0]);
    for (const [f, c] of Object.entries(signed.files)) files[`engine/${wf.name}/${f}`] = c;
  }
  files['workflows/slots.yaml'] = toYaml({ kind: 'slot-fills', slots });

  // The native assemblies of the platform's, the schemes' and the bank's native activities, each with its SHA-256 (R-09-120,
  // R-09-126). A promotable distribution carries every one; a test distribution says which it does not.
  const native = nativeModules(all.map((e) => e.doc), scopeOfName, !req.withSimulators);
  if (native.problems.length) throw new DistributionRefused(`The distribution of ${bank} is refused: ${native.problems.join('; ')}`);
  Object.assign(files, native.files);
  for (const a of native.artefacts) {
    const pk = a.scope === 'b' ? bankPackOf(env, bank)?.doc : env.store.byKind('pack').find((e) => e.doc.scope === a.scope)?.doc;
    artefacts.push({ ...a, release: pk?.release ?? '1.0.0' });
  }

  const id = req.id ?? nextDistributionId(env, bank, req.date);
  if (!DIST_ID.test(id)) throw new DistributionRefused(`${id} is not a distribution id (YYYY.MM.DD-n)`);
  const packs = scopes.map((s) => {
    const m = s === 'b' ? bankPackOf(env, bank)?.doc : env.store.byKind('pack').find((e) => e.doc.scope === s)?.doc;
    return { scope: s, release: m?.release ?? '1.0.0', ...(s === 'b' ? { tenant: bank } : {}), artefacts: (byScope.get(s) ?? []).length };
  });
  const billOfMaterials = { packs, artefacts: artefacts.sort((a, b) => a.path.localeCompare(b.path)) };
  const manifest = {
    kind: 'distribution', id, bank, engine, dsl: '1.0.0', runtime: '>=1.0.0',
    schemes, platform: platformScopes(schemes),
    billOfMaterials,
    parameterContract: params.contract,
    slotContracts: slot.fills,
    native: native.section,
    longestWait: longestWait(params.contract),
    changeSets: req.changeSets ?? [],
    promotable: !req.withSimulators,
  };
  files['manifest.yaml'] = toYaml(manifest);
  const claims: ReleaseClaims = { kind: 'distribution', releaseId: `dist/${bank}@${id}`, workflow: '', release: id, target: engine, tenant: bank, changeSet: req.changeSets?.[0] ?? null, extra: { billOfMaterials, parameterContract: params.contract } };
  const signed = signFiles(files, claims) as Record<string, string>;
  const st = JSON.parse(signed['release.json']);
  return {
    kind: 'distribution', bank, id, releaseId: claims.releaseId, folder: distFolder(bank, id), files: signed, manifest,
    tag: { name: distTag(bank, id), message: [`Distribution ${claims.releaseId}`, '', `Folder: ${distFolder(bank, id)}/`, `Engine: ${engine}`, `Schemes: ${schemes.join(', ') || 'none'}`, `Change sets: ${(req.changeSets ?? []).join(', ') || 'none'}`, `Signed by key: ${st.keyId}`, `Statement sha256: ${sha(signed['release.json']).slice(7)}`].join('\n') },
  };
}

/**
 * Builds a bank's configuration release (R-09-51, R-11-40): its configuration set (dials and rule values, each with its
 * validFrom), its port bindings (adapter and settings, credentials by secretRef only) and calendars, checked against the
 * parameter contract of each distribution named, which it records as `requiresContract`.
 */
export function buildConfigurationRelease(env: StudioEnv, req: { bank: string; release?: string; changeSet?: string; against: { releaseId: string; parameterContract: any }[]; bindings?: any[]; calendars?: any[]; set?: any }): BuiltBundle {
  const { bank } = req;
  env = scopedEnv(env, bank);
  const set = clone(req.set ?? env.store.configurationSet(bank));
  if (!set) throw new DistributionRefused(`${bank} has no configuration set`);
  // A value read from a reference table goes out as the values it reads, one per edition (R-11-51).
  set.values = resolveReferenceValues(set.values ?? [], (n) => env.store.doc(n, undefined, bank));
  // The bank's port bindings go out in its configuration release (R-11-47): those given, else the ones the studio last released.
  // A release of the bindings names the configuration release with build metadata, so that one with the same values and
  // other bindings is a release of its own: <configuration set release>+bindings.<port bindings release>.
  const released = req.bindings === undefined ? env.store.portBindings(bank) : undefined;
  if (req.bindings === undefined && released?.bindings?.length) req = { ...req, bindings: released.bindings };
  const release = req.release ?? (released?.release ? `${set.release}+bindings.${released.release}` : set.release);
  set.release = req.release ?? set.release;
  if (!req.against.length) throw new DistributionRefused(`A configuration release of ${bank} is checked against a distribution's parameter contract: build one first`);
  const problems: string[] = [];
  for (const v of set.values ?? []) {
    const declaring = req.against.filter((d) => (d.parameterContract?.parameters ?? []).some((p: any) => p.parameter === v.parameter));
    if (!declaring.length) problems.push(`${v.parameter} is not a dial of ${req.against.map((d) => d.releaseId).join(', ')}`);
    for (const d of declaring) {
      const decl = d.parameterContract.parameters.find((p: any) => p.parameter === v.parameter);
      if (!withinBounds(decl, v.value)) problems.push(`${v.parameter} = ${JSON.stringify(v.value)} is outside its bounds ${JSON.stringify(decl.bounds)} in ${d.releaseId}`);
    }
    if (!v.validFrom) problems.push(`${v.parameter} has no validFrom`);
  }
  const isPort = (n: string) => env.store.all().some((e) => e.kind === 'port' && e.name === n && e.status !== 'draft' && (e.tenant === OWNER_TENANT || e.tenant === bank));
  if (req.bindings?.length) problems.push(...portBindingProblems({ bindings: req.bindings }, isPort));
  for (const b of req.bindings ?? []) {
    if (!b.validFrom) problems.push(`the binding of ${b.port} has no validFrom`);
    for (const [k, val] of Object.entries<any>(b.settings ?? {})) if (/(password|passphrase|secret|token|apikey|accesskey|privatekey|credential)$/i.test(k)) problems.push(`the binding of ${b.port} holds ${k} as a setting: give it as secretRef`);
    if (b.secretRef !== undefined && !/^(env|file|vault|akv|aws-sm|aws-ssm):/.test(String(b.secretRef))) problems.push(`the binding of ${b.port} holds a secretRef that is not a reference`);
  }
  if (problems.length) throw new DistributionRefused(`The configuration release ${bank}@${release} is refused: ${problems.join('; ')}`);
  const files: Record<string, string> = {};
  files['configuration/configuration-set.yaml'] = toYaml(set);
  if (req.bindings?.length) files['bindings/port-bindings.yaml'] = toYaml({ kind: 'port-bindings', tenant: bank, ...(released?.release ? { release: released.release } : {}), bindings: req.bindings });
  for (const c of req.calendars ?? []) files[`calendars/${c.name}.yaml`] = toYaml(c);
  // The reference tables the bank sees, each at its latest release with every edition (R-01-25): the runtime serves them by
  // date under GET /lookups/{domain}.
  for (const t of referenceTablesFor(env, bank)) files[`reference/${t.name}.yaml`] = toYaml(t);
  const requiresContract = req.against.map((d) => ({ distribution: d.releaseId, sha256: sha(canonicalJson(d.parameterContract)) })).sort((a, b) => a.distribution.localeCompare(b.distribution));
  const billOfMaterials = { artefacts: Object.entries(files).map(([p, t]) => ({ path: p, sha256: sha(t) })).sort((a, b) => a.path.localeCompare(b.path)) };
  const manifest = { kind: 'configuration-release', bank, release, tenant: bank, requiresContract, billOfMaterials, changeSet: req.changeSet ?? null, values: (set.values ?? []).length, bindings: (req.bindings ?? []).length, referenceTables: Object.keys(files).filter((f) => f.startsWith('reference/')).length, ...(released?.release ? { portBindings: released.release } : {}) };
  files['manifest.yaml'] = toYaml(manifest);
  const releaseId = `config/${bank}@${release}`;
  const signed = signFiles(files, { kind: 'configuration', releaseId, workflow: '', release, target: '', tenant: bank, changeSet: req.changeSet ?? null, extra: { billOfMaterials } }) as Record<string, string>;
  return {
    kind: 'configuration', bank, id: release, releaseId, folder: configFolder(bank, release), files: signed, manifest,
    tag: { name: configTag(bank, release), message: [`Configuration release ${releaseId}`, '', `Folder: ${configFolder(bank, release)}/`, `Checked against: ${requiresContract.map((r) => r.distribution).join(', ')}`].join('\n') },
  };
}

/** The reference tables a bank sees: shared ones and its own, the latest published release of each name. */
export function referenceTablesFor(env: StudioEnv, bank: string): any[] {
  const latest = new Map<string, Entry>();
  for (const e of env.store.byKind('reference-table')) {
    if (e.tenant !== OWNER_TENANT && e.tenant !== bank) continue;
    const have = latest.get(e.name);
    if (!have || compareReleases(e.release, have.release) > 0) latest.set(e.name, e);
  }
  return [...latest.values()].sort((a, b) => a.name.localeCompare(b.name)).map((e) => e.doc);
}

/** The distributions of a bank the content branch holds, with their manifests: what a configuration release is checked against. */
export function publishedDistributions(env: StudioEnv, bank: string): { releaseId: string; id: string; manifest: any; parameterContract: any }[] {
  const out: any[] = [];
  const files = env.store.content?.files;
  const extra = (env.store.extra.distributionManifests ?? {}) as Record<string, any>;
  for (const [k, m] of Object.entries(extra)) if (m.bank === bank) out.push({ releaseId: k, id: m.id, manifest: m, parameterContract: m.parameterContract });
  if (files) {
    for (const [p, t] of files) {
      const m = new RegExp(`^${DIST_ROOT}/${bank}/([^/]+)/manifest\\.yaml$`).exec(p);
      if (!m || out.some((x) => x.id === m[1])) continue;
      const doc = parseYaml(t);
      out.push({ releaseId: `dist/${bank}@${m[1]}`, id: m[1], manifest: doc, parameterContract: doc.parameterContract });
    }
  }
  return out.sort((a, b) => a.id.localeCompare(b.id, undefined, { numeric: true }));
}


/** The file changes and the tag of a built bundle, under the content root, for one commit (R-09-21: created, never rewritten). */
export function bundleFiles(b: BuiltBundle): Record<string, string> {
  return Object.fromEntries(Object.entries(b.files).map(([f, c]) => [`${b.folder}/${f}`, c]));
}

/** Remembers what a publish wrote, so the next id and the next configuration release see it before the branch is read again. */
export function remember(env: StudioEnv, b: BuiltBundle) {
  if (b.kind === 'distribution') {
    (env.store.extra.distributions ??= []).push(`${b.bank}/${b.id}`);
    (env.store.extra.distributionManifests ??= {})[b.releaseId] = b.manifest;
  }
}

/** Is a change set's member a bank's own artefact (not its values)? Shared platform or scheme content? Its values? */
export function classify(members: { kind: string; name: string }[], tenant: string) {
  // A bank's own reference table (its calendar, its cut-offs, its branches) is values: it goes out in the bank's configuration
  // release, never in a distribution (R-01-25, R-11-52), so approving one builds a configuration release and no distribution.
  const isValues = (m: { kind: string }) => m.kind === 'configuration-set' || m.kind === 'port-bindings' || (tenant !== OWNER_TENANT && m.kind === 'reference-table');
  const values = members.some(isValues);
  const bankPack = tenant !== OWNER_TENANT && members.some((m) => !isValues(m));
  const shared = tenant === OWNER_TENANT ? [...new Set(members.map((m) => scopeOfName(m.name)).filter((s) => s !== 'b'))] : [];
  return { values, bankPack, shared };
}

/** The banks a shared change takes: a platform change every bank, a scheme change the banks that license the scheme. */
export function banksTaking(env: StudioEnv, scopes: string[]): string[] {
  return banks(env).filter((b) => {
    let lic: string[] = [];
    try { lic = licensedSchemes(env, b); } catch { return false; }
    return scopes.some((s) => s === 'p' || s.endsWith('.p') ? lic.some((l) => l.startsWith(`${s.split('.')[0]}.`)) || s === 'p' : lic.includes(s));
  });
}

