import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { canonicalJson } from '../core/canonical.js';
import { parseName, parseScope } from '../core/names.js';
import { keyOf, Store } from '../core/store.js';
import { parseYamlAll } from '../core/yaml.js';
import { checkDocument, interfaceDiff, proposeRelease } from '../checks/checks.js';
import type { StudioEnv } from '../env.js';
import { unzip } from '../export/zip.js';
import { saveSample, saveScript } from '../governance/authoring.js';
import * as gov from '../governance/governance.js';
import { lintScript } from '../interp/sandbox.js';
import { runTest } from '../testing/runner.js';

// Import (docs/ISSUES.md I-33). An extraction bundle is what an AI agent writes when it is pointed at
// an existing scheme's source (docs/import/EXTRACTION-PROMPT.md): the studio's own documents, the
// scripts and samples behind them, tests, a bank's configuration, an inventory that says where every
// item of the source went, and its findings. The import reads the bundle, checks it hard, runs its
// tests against its own documents, and saves everything as drafts. It never publishes: maker-checker
// does that. Comprehensiveness is checked, not trusted: every source file is inventoried or excluded
// with a reason, and every document is explained by an item of the source.

export const FORMAT = 1;

export interface BundleFile { name: string; content: string }
export interface Bundle {
  manifest: any;
  inventory: any[];
  findings: any[];
  docs: { file: string; doc: any }[];
  configuration: { file: string; doc: any }[];
  tests: { file: string; doc: any }[];
  scripts: { file: string; activity: string; language: 'typescript' | 'python'; source: string }[];
  samples: { name: string; content: string }[];
  problems: Issue[];
}

export interface Issue { severity: 'error' | 'warning' | 'info'; code: string; where: string; message: string }

/** Every target an inventory item may have. */
export const TARGETS = ['artefact', 'parameter', 'configuration', 'bank-rule', 'bank-hook', 'sample', 'test', 'infrastructure', 'retire', 'unmapped'] as const;
/** What a source item may be. */
export const SOURCE_KINDS = ['workflow', 'activity', 'service', 'rule', 'table', 'map', 'message', 'port', 'config', 'error', 'retry', 'timer', 'state', 'schedule', 'test', 'sample', 'script', 'ui', 'infrastructure', 'other'] as const;

// ------------------------------------------------------------------------ reading

/** Read a bundle from a zip, or from files already read (name relative to the bundle root). */
export function readBundle(input: Buffer | BundleFile[]): Bundle {
  const files: BundleFile[] = Buffer.isBuffer(input)
    ? unzip(input).filter((e) => !e.name.endsWith('/')).map((e) => ({ name: e.name, content: e.content.toString('utf8') }))
    : input;
  // A zip may hold the bundle in one top folder: strip it.
  const top = files.length && files.every((f) => f.name.includes('/') && f.name.split('/')[0] === files[0].name.split('/')[0]) && !files.some((f) => f.name === 'extraction.yaml')
    ? `${files[0].name.split('/')[0]}/` : '';
  const rel = files.map((f) => ({ name: f.name.slice(top.length).replace(/^\.\//, ''), content: f.content }));
  const b: Bundle = { manifest: undefined, inventory: [], findings: [], docs: [], configuration: [], tests: [], scripts: [], samples: [], problems: [] };
  const yamlDocs = (f: BundleFile) => {
    try {
      return parseYamlAll(f.content).filter((d) => d !== null && d !== undefined);
    } catch (e: any) {
      b.problems.push({ severity: 'error', code: 'I01', where: f.name, message: `The YAML does not parse: ${e.message}` });
      return [];
    }
  };
  for (const f of rel) {
    const [dir] = f.name.split('/');
    if (f.name === 'extraction.yaml') b.manifest = yamlDocs(f)[0];
    else if (f.name === 'inventory.yaml') b.inventory = (yamlDocs(f)[0]?.items ?? yamlDocs(f)[0] ?? []) as any[];
    else if (f.name === 'findings.yaml') b.findings = (yamlDocs(f)[0]?.findings ?? yamlDocs(f)[0] ?? []) as any[];
    else if (dir === 'artefacts' && /\.ya?ml$/.test(f.name)) for (const doc of yamlDocs(f)) b.docs.push({ file: f.name, doc });
    else if (dir === 'configuration' && /\.ya?ml$/.test(f.name)) for (const doc of yamlDocs(f)) b.configuration.push({ file: f.name, doc });
    else if (dir === 'tests' && /\.ya?ml$/.test(f.name)) for (const doc of yamlDocs(f)) b.tests.push({ file: f.name, doc });
    else if (dir === 'scripts' && /\.(ts|py)$/.test(f.name)) {
      const base = path.basename(f.name);
      b.scripts.push({ file: f.name, activity: base.replace(/\.(ts|py)$/, ''), language: base.endsWith('.py') ? 'python' : 'typescript', source: f.content });
    } else if (dir === 'samples' && f.name.split('/').length === 2) b.samples.push({ name: path.basename(f.name), content: f.content });
    else if (f.name === 'README.md' || dir === 'notes') { /* for people */ }
    else b.problems.push({ severity: 'warning', code: 'I02', where: f.name, message: 'Not part of the bundle format; ignored. See docs/import/EXTRACTION-PROMPT.md, "The bundle".' });
  }
  return b;
}

/** Read a bundle from a folder on disk. */
export function readBundleDir(dir: string): Bundle {
  const out: BundleFile[] = [];
  const walk = (d: string) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const f = path.join(d, e.name);
      if (e.isDirectory()) walk(f);
      else out.push({ name: path.relative(dir, f).split(path.sep).join('/'), content: fs.readFileSync(f, 'utf8') });
    }
  };
  walk(dir);
  return readBundle(out);
}

// ------------------------------------------------------------------------ checks

/** Personal data a synthetic bundle must not hold (spec guard-rail: no personal data anywhere). */
const PERSONAL: [RegExp, string][] = [
  [/[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/, 'an e-mail address'],
  [/\b784[- ]?\d{4}[- ]?\d{7}[- ]?\d\b/, 'an Emirates ID number'],
  [/(\+|00)971[\s-]?\d{1,2}[\s-]?\d{3}[\s-]?\d{4}\b/, 'a UAE telephone number'],
  [/\b(?:4\d{3}|5[1-5]\d{2})[ -]?\d{4}[ -]?\d{4}[ -]?\d{4}\b/, 'a card number'],
];
function personalData(text: string): string[] {
  return PERSONAL.filter(([re]) => re.test(text)).map(([, what]) => what);
}

const PY_FORBIDDEN = [/\bimport\s+(os|sys|time|random|socket|subprocess|datetime)\b/, /\bfrom\s+(os|sys|time|random|socket|subprocess|datetime)\b/, /\bopen\(/];

const nameOf = (d: any) => d?.name ?? (d?.kind === 'scheme' ? `scheme:${d.scope}` : d?.kind === 'configuration-set' ? `configuration:${d.tenant ?? ''}` : d?.id ? `test:${d.id}` : undefined);

export interface Item {
  key: string;
  kind: string;
  name: string;
  release?: string;
  file: string;
  tenant: string;
  /** new, unchanged (the same as a published release), changed (a new release of a published line) or replaces-draft */
  status: 'new' | 'unchanged' | 'changed' | 'replaces-draft';
  existing?: string;
  proposedRelease?: string;
  sources: string[];
  issues: Issue[];
}

export interface Report {
  ok: boolean;
  manifest: { scope?: string; source?: any; extractedBy?: any; validatedBy?: any; format?: number };
  summary: Record<string, number>;
  items: Item[];
  scripts: { activity: string; language: string; issues: Issue[] }[];
  samples: { name: string; issues: Issue[] }[];
  tests: { id: string; target: string; passed?: boolean; outcome?: string; error?: string; failed?: string[] }[];
  inventory: {
    total: number;
    byTarget: Record<string, number>;
    bySourceKind: Record<string, number>;
    reuse: Record<string, number>;
    unmapped: any[];
    retired: any[];
    infrastructure: any[];
    sourceFiles: { total: number; inventoried: number; excluded: number; missing: string[] };
  };
  findings: any[];
  issues: Issue[];
}

export interface ImportOptions {
  /** The tenant shared content is imported into (the owner). */
  tenant: string;
  /** The bank tenant that receives scope `b` content and configuration sets, if the bundle has any. */
  bankTenant?: string;
  /** Run the bundle's tests against its own documents (default true). */
  runTests?: boolean;
}

function tenantFor(doc: any, opts: ImportOptions): string {
  if (doc?.kind === 'configuration-set') return opts.bankTenant ?? '';
  const scope = doc?.name ? parseName(doc.name)?.scope : doc?.scope;
  return scope === 'b' ? opts.bankTenant ?? '' : opts.tenant;
}

/** Everything the import knows before it saves anything. */
export async function analyse(env: StudioEnv, b: Bundle, opts: ImportOptions): Promise<Report> {
  const issues: Issue[] = [...b.problems];
  const err = (code: string, where: string, message: string) => issues.push({ severity: 'error', code, where, message });
  const warn = (code: string, where: string, message: string) => issues.push({ severity: 'warning', code, where, message });
  const m = b.manifest ?? {};

  // The manifest.
  if (!b.manifest) err('I10', 'extraction.yaml', 'The bundle has no extraction.yaml manifest');
  else {
    if (m.kind !== 'extraction') err('I10', 'extraction.yaml', 'kind must be extraction');
    if (m.format !== FORMAT) err('I10', 'extraction.yaml', `format must be ${FORMAT}`);
    if (!m.scope || parseScope(m.scope)?.level !== 'scheme') err('I10', 'extraction.yaml', 'scope must name the scheme the bundle builds, for example uae.wps');
    for (const k of ['system', 'repository', 'revision']) if (!m.source?.[k]) err('I10', 'extraction.yaml', `source.${k} is required: the import records where the content came from`);
    if (!Array.isArray(m.source?.files) || !m.source.files.length) err('I11', 'extraction.yaml', 'source.files must list every file of the source that was read: comprehensiveness is checked against it');
    if (m.dataPolicy !== 'synthetic') err('I12', 'extraction.yaml', 'dataPolicy must be synthetic: samples and tests hold made-up values only');
    if (!m.extractedBy?.tool) warn('I10', 'extraction.yaml', 'extractedBy.tool is missing: say what extracted it');
    if (!m.validatedBy?.expert) warn('I13', 'extraction.yaml', 'No expert has validated the extraction yet (validatedBy). Import is allowed; publish should wait for it.');
  }
  const scope: string | undefined = m.scope;
  const cluster = scope?.split('.')[0];
  const allowedScopes = new Set(['p', `${cluster}.p`, scope, 'b']);
  if (b.docs.some((d) => (d.doc?.name ? parseName(d.doc.name)?.scope : d.doc?.scope) === 'b') || b.configuration.length) {
    if (!opts.bankTenant) err('I14', 'bundle', 'The bundle has bank content (scope b or a configuration set): name the bank tenant to import it into');
  }

  // A shadow of the catalogue, in memory only, with the bundle in it as if it were published: what
  // a bank's configuration and the tests will meet once a checker has approved it. Nothing is written.
  const shadow = env.store.shadow();
  for (const { doc } of [...b.docs, ...b.tests, ...b.configuration]) {
    if (doc && typeof doc === 'object' && doc.kind) shadow.add(doc, 'import', 'studio', false, 'published', tenantFor(doc, opts) || opts.tenant);
  }
  const senv: StudioEnv = { ...env, store: shadow };

  // Documents, with the catalogue and the rest of the bundle around them.
  const all = [...b.docs, ...b.tests].map((d) => d.doc);
  const sourcesOf = new Map<string, string[]>();
  for (const it of b.inventory) {
    const t = it?.target?.name;
    if (t) sourcesOf.set(t, [...(sourcesOf.get(t) ?? []), `${it.source?.path ?? '?'}${it.source?.lines ? `:${it.source.lines}` : ''}`]);
  }
  const items: Item[] = [];
  const seen = new Set<string>();
  for (const { file, doc } of [...b.docs, ...b.configuration, ...b.tests]) {
    const name = nameOf(doc) ?? '(no name)';
    const key = doc && typeof doc === 'object' ? keyOf(doc) : file;
    const tenant = tenantFor(doc, opts);
    const it: Item = { key, kind: doc?.kind ?? '?', name, release: doc?.release, file, tenant, status: 'new', sources: sourcesOf.get(name) ?? sourcesOf.get(doc?.id) ?? [], issues: [] };
    const ierr = (code: string, message: string) => it.issues.push({ severity: 'error', code, where: name, message });
    const iwarn = (code: string, message: string) => it.issues.push({ severity: 'warning', code, where: name, message });
    if (!doc || typeof doc !== 'object' || !doc.kind) { ierr('I20', 'Not a studio document: it has no kind'); items.push(it); continue; }
    if (seen.has(key)) ierr('I21', `${key} is in the bundle twice`);
    seen.add(key);
    // Scope: the bundle builds one scheme, with what it adds to its cluster platform, p, and the bank.
    const docScope = doc.name ? parseName(doc.name)?.scope : doc.scope;
    if (doc.name && !parseName(doc.name)) ierr('L01', `${doc.name} is not a name: <scope>.<type>.<name>.v<major>`);
    if (docScope && !allowedScopes.has(docScope)) ierr('I22', `Scope ${docScope} is outside this bundle: ${scope}, ${cluster}.p, p or b`);
    // Static checks, with the rest of the bundle in place: references resolve inside it.
    const tenantForChecks = tenant || opts.tenant;
    for (const i of checkDocument(env, doc, tenantForChecks, all.filter((x) => x !== doc))) {
      (i.severity === 'error' ? ierr : iwarn)(i.code, i.message);
    }
    if (doc.kind === 'configuration-set' && opts.bankTenant) {
      for (const i of gov.checkConfiguration(senv, opts.bankTenant, doc)) (i.severity === 'error' ? ierr : iwarn)(i.code, i.message);
    }
    for (const what of personalData(JSON.stringify(doc))) ierr('I30', `It holds what looks like ${what}. Use synthetic values only.`);
    if (doc.kind === 'scheme') {
      const planned = (doc.messages ?? []).filter((x: any) => x.disposition?.planned).map((x: any) => x.id);
      if (planned.length) iwarn('I53', `It has ${planned.length} planned message(s) (${planned.join(', ')}): it imports, but cannot be published until they are built or retired (R-05-22)`);
    }
    // Traceability: every document is explained by an item of the source.
    if (doc.kind !== 'test' && doc.kind !== 'configuration-set' && !it.sources.length) ierr('I40', 'No item of the inventory maps to it: say which source it came from, or leave it out');
    // The catalogue: new, the same as a release already published, or a new release of it.
    if (doc.name || doc.kind === 'scheme') {
      const existing = doc.kind === 'scheme' ? env.store.all().filter((e) => e.kind === 'scheme' && e.doc.scope === doc.scope && e.status === 'published').pop() : env.store.latest(doc.name, tenantForChecks);
      const draft = env.store.drafts(tenantForChecks).find((e) => e.key === key);
      if (draft) it.status = 'replaces-draft';
      if (existing && existing.status === 'published') {
        it.existing = `${existing.name}@${existing.release}`;
        const same = canonicalJson({ ...existing.doc, provenance: undefined }) === canonicalJson({ ...doc, provenance: undefined });
        if (same) it.status = 'unchanged';
        else {
          it.status = 'changed';
          let required: 'major' | 'minor' | 'patch' = 'minor';
          try { required = (interfaceDiff(existing.doc, doc) as any).required ?? 'minor'; } catch { /* keep minor */ }
          it.proposedRelease = proposeRelease(existing.release, required as any);
          if (existing.release === doc.release) ierr('R-01-10', `${existing.name}@${existing.release} is published with different content; a published release is never edited. Give this one release ${it.proposedRelease}.`);
        }
      }
    }
    items.push(it);
  }

  // Scripts: each belongs to an activity bound to a script, and uses only the injected interface.
  const docsByName = new Map(b.docs.map((d) => [d.doc?.name, d.doc]));
  const scripts = b.scripts.map((s) => {
    const sIssues: Issue[] = [];
    const act = docsByName.get(s.activity) ?? env.store.latest(s.activity)?.doc;
    if (!act) sIssues.push({ severity: 'error', code: 'I50', where: s.file, message: `No activity ${s.activity} in the bundle or the catalogue` });
    else if (act.implementation?.binding !== 'script') sIssues.push({ severity: 'error', code: 'I50', where: s.file, message: `${s.activity} is not bound to a script` });
    else if (act.implementation?.language && act.implementation.language !== s.language) sIssues.push({ severity: 'error', code: 'I50', where: s.file, message: `${s.activity} declares ${act.implementation.language}` });
    const lint = s.language === 'typescript' ? lintScript(s.source) : PY_FORBIDDEN.filter((re) => re.test(s.source)).map((re) => `The script uses ${re.source}, outside the injected interface`);
    for (const l of lint) sIssues.push({ severity: 'error', code: 'L28', where: s.file, message: l });
    const entry = s.language === 'typescript' ? /export\s+(async\s+)?function\s+run\s*\(/ : /^(async\s+)?def\s+run\s*\(/m;
    if (!entry.test(s.source)) sIssues.push({ severity: 'error', code: 'I51', where: s.file, message: 'A script defines run(...) and returns the outcome' });
    for (const what of personalData(s.source)) sIssues.push({ severity: 'error', code: 'I30', where: s.file, message: `It holds what looks like ${what}` });
    if (!sourcesOf.get(s.activity)) sIssues.push({ severity: 'warning', code: 'I40', where: s.file, message: 'No item of the inventory names the code this script was translated from' });
    return { activity: s.activity, language: s.language, issues: sIssues };
  });
  // Activities bound to a script that the bundle gives no script for run on the reference behaviour.
  for (const { doc } of b.docs) {
    if (doc?.kind === 'activity' && doc.implementation?.binding === 'script' && !b.scripts.some((s) => s.activity === doc.name) && !env.scripts.find(doc.name)) {
      warn('I52', doc.name, 'Bound to a script, but the bundle has none: in the studio it runs on the reference behaviour for its type. Translate the source code into a script-behind.');
    }
  }

  // Samples: synthetic, named for the bench.
  const samples = b.samples.map((s) => {
    const sIssues: Issue[] = [];
    if (!/^[a-z0-9][a-z0-9-]*\.[a-z0-9]+$/.test(s.name)) sIssues.push({ severity: 'error', code: 'C12', where: s.name, message: 'A sample name is lower case, digits and hyphens, with an extension' });
    if (env.store.factorySamples.has(s.name)) sIssues.push({ severity: 'error', code: 'R-01-10', where: s.name, message: `samples/${s.name} is factory content; choose another name` });
    for (const what of personalData(s.content)) sIssues.push({ severity: 'error', code: 'I30', where: s.name, message: `It holds what looks like ${what}. A sample is synthetic.` });
    return { name: s.name, issues: sIssues };
  });

  // The inventory: every item mapped, retired, sent to infrastructure, or listed as unmapped.
  const inv = { total: b.inventory.length, byTarget: {} as Record<string, number>, bySourceKind: {} as Record<string, number>, reuse: {} as Record<string, number>, unmapped: [] as any[], retired: [] as any[], infrastructure: [] as any[], sourceFiles: { total: 0, inventoried: 0, excluded: 0, missing: [] as string[] } };
  const ids = new Set<string>();
  for (const it of b.inventory) {
    const where = `inventory ${it?.id ?? '?'}`;
    if (!it?.id) err('I60', where, 'Every item has an id');
    else if (ids.has(it.id)) err('I60', where, `Item id ${it.id} is used twice`);
    ids.add(it?.id);
    if (!it?.source?.path) err('I60', where, 'source.path is required: the file the item is in');
    if (!SOURCE_KINDS.includes(it?.source?.kind)) err('I60', where, `source.kind must be one of ${SOURCE_KINDS.join(', ')}`);
    const t = it?.target?.kind;
    if (!TARGETS.includes(t)) { err('I60', where, `target.kind must be one of ${TARGETS.join(', ')}`); continue; }
    inv.byTarget[t] = (inv.byTarget[t] ?? 0) + 1;
    inv.bySourceKind[it.source.kind] = (inv.bySourceKind[it.source.kind] ?? 0) + 1;
    if (it.reuse) inv.reuse[it.reuse] = (inv.reuse[it.reuse] ?? 0) + 1;
    const name = it.target.name;
    const inBundle = (n: string) => b.docs.some((d) => nameOf(d.doc) === n) || b.tests.some((d) => nameOf(d.doc) === n || d.doc?.id === n) || b.configuration.some((d) => d.doc?.release && n === 'configuration');
    switch (t) {
      case 'artefact':
      case 'bank-rule':
      case 'bank-hook':
        if (!name) err('I61', where, `target.name is required for ${t}`);
        else if (!inBundle(name) && !env.store.latest(name)) err('I61', where, `It maps to ${name}, which is neither in the bundle nor in the catalogue`);
        else if (t !== 'artefact' && parseName(name)?.scope !== 'b') err('I61', where, `A ${t} is bank content: its name has scope b`);
        if (it.reuse && !['as-is', 'with-change', 'new'].includes(it.reuse)) err('I61', where, 'reuse is as-is, with-change or new');
        break;
      case 'parameter': {
        const [art, par] = String(name ?? '').split('#');
        const doc = b.docs.find((d) => d.doc?.name === art)?.doc ?? env.store.latest(art)?.doc;
        if (!doc || !doc.parameters?.[par]) err('I61', where, `It maps to the parameter ${name}, which no rule or table in the bundle or catalogue declares`);
        break;
      }
      case 'sample': if (!b.samples.some((s) => s.name === name)) err('I61', where, `It maps to the sample ${name}, which the bundle does not have`); break;
      case 'test': if (!inBundle(name) && !env.store.test(name)) err('I61', where, `It maps to the test ${name}, which is neither in the bundle nor the catalogue`); break;
      case 'retire': inv.retired.push(it); if (!it.note) err('I62', where, 'A retired item says why'); break;
      case 'infrastructure': inv.infrastructure.push(it); if (!it.note) warn('I62', where, 'Say what the infrastructure item needs'); break;
      case 'unmapped': inv.unmapped.push(it); if (!it.note) err('I62', where, 'An unmapped item says why it could not be mapped'); break;
    }
  }
  if (inv.unmapped.length) warn('I63', 'inventory', `${inv.unmapped.length} item(s) of the source are not mapped yet; they are listed for the expert`);
  // Every file of the source is inventoried, or excluded with a reason.
  const files: string[] = (m.source?.files ?? []).map((f: any) => (typeof f === 'string' ? f : f?.path)).filter(Boolean);
  const excluded = new Map<string, string>((m.source?.excluded ?? []).map((x: any) => [x.path, x.why]));
  const inventoried = new Set(b.inventory.map((it) => it?.source?.path));
  inv.sourceFiles.total = files.length;
  for (const f of files) {
    if (inventoried.has(f)) inv.sourceFiles.inventoried++;
    else if (excluded.has(f)) { inv.sourceFiles.excluded++; if (!excluded.get(f)) err('I64', f, 'An excluded file says why'); }
    else inv.sourceFiles.missing.push(f);
  }
  for (const f of inv.sourceFiles.missing) err('I64', f, 'A source file is neither inventoried nor excluded: the extraction is not comprehensive');
  for (const p of inventoried) if (p && files.length && !files.includes(p)) warn('I64', p, 'The inventory cites a file that source.files does not list');

  for (const f of b.findings) {
    if (!['difference', 'question', 'error-scenario', 'risk', 'decision'].includes(f?.kind)) warn('I70', 'findings.yaml', `A finding's kind is difference, question, error-scenario, risk or decision: ${JSON.stringify(f).slice(0, 80)}`);
    for (const what of personalData(JSON.stringify(f))) err('I30', 'findings.yaml', `A finding holds what looks like ${what}`);
  }

  // The bundle's tests, against its own documents, scripts and samples, before anything is saved.
  const tests: Report['tests'] = [];
  if (opts.runTests !== false && b.tests.length) {
    // In memory: the bundle's scripts and samples are searched first, nothing is written (R-11-24).
    const bscripts = b.scripts.map((s) => ({ name: s.activity, language: s.language, source: s.source, file: `bundle:scripts/${s.activity}.${s.language === 'python' ? 'py' : 'ts'}` }));
    const overlay = Object.fromEntries(b.samples.filter((s) => /^[a-z0-9][a-z0-9-]*\.[a-z0-9]+$/.test(s.name)).map((s) => [s.name, s.content]));
    const tenv: StudioEnv = { ...senv, scripts: env.scripts.withFirst(bscripts), sampleOverlay: { ...(env.sampleOverlay ?? {}), ...overlay } };
    for (const { doc } of b.tests) {
      try {
        const r = await runTest(tenv, doc);
        tests.push({ id: doc.id, target: doc.target, passed: r.passed, outcome: r.trace?.at(-1), error: r.error?.split('\n')[0] ?? (r.record?.instances ?? []).map((i: any) => i.failed).filter(Boolean)[0], failed: r.checks.filter((c: any) => !c.passed).map((c: any) => `${c.what}: expected ${JSON.stringify(c.expected)}, got ${JSON.stringify(c.actual)}`) });
      } catch (e: any) {
        tests.push({ id: doc?.id, target: doc?.target, passed: false, error: String(e?.message ?? e) });
      }
    }
    for (const t of tests) if (!t.passed) warn('I80', `test ${t.id}`, `Fails against the bundle's own documents: ${t.error ?? t.failed?.join('; ') ?? ''}`);
  }

  const all_issues = [...issues, ...items.flatMap((i) => i.issues), ...scripts.flatMap((s) => s.issues), ...samples.flatMap((s) => s.issues)];
  const count = (k: string) => items.filter((i) => i.kind === k).length;
  return {
    ok: !all_issues.some((i) => i.severity === 'error'),
    manifest: { scope, source: m.source ? { ...m.source, files: undefined, fileCount: files.length } : undefined, extractedBy: m.extractedBy, validatedBy: m.validatedBy, format: m.format },
    summary: {
      documents: b.docs.length, activities: count('activity'), rules: count('rule'), ruleSets: count('ruleset'), tables: count('table'), workflows: count('workflow'),
      hooks: count('hook'), ports: count('port'), simulators: count('simulator'), extensions: count('extension'), schemes: count('scheme'),
      configurationSets: b.configuration.length, tests: b.tests.length, scripts: b.scripts.length, samples: b.samples.length, findings: b.findings.length,
      new: items.filter((i) => i.status === 'new').length, unchanged: items.filter((i) => i.status === 'unchanged').length, changed: items.filter((i) => i.status === 'changed').length,
      testsPassed: tests.filter((t) => t.passed).length, testsFailed: tests.filter((t) => !t.passed).length,
      errors: all_issues.filter((i) => i.severity === 'error').length, warnings: all_issues.filter((i) => i.severity === 'warning').length,
    },
    items, scripts, samples, tests, inventory: inv, findings: b.findings, issues,
  };
}

// ------------------------------------------------------------------------ applying

export interface ApplyOptions extends ImportOptions {
  actor: string;
  bankActor?: string;
  /** Import even with errors (the drafts then show them); default false. */
  force?: boolean;
}

/**
 * Save the bundle as drafts: documents (new and changed; unchanged ones are reused as they are),
 * then scripts, samples, tests and configuration sets. Nothing is published. Returns what was saved
 * and the change sets a checker will approve.
 */
export async function apply(env: StudioEnv, b: Bundle, opts: ApplyOptions) {
  const report = await analyse(env, b, opts);
  if (!report.ok && !opts.force) return { applied: false, report, saved: [] as string[], skipped: [] as string[], changeSets: [] as string[][] };
  const m = b.manifest ?? {};
  const note = `Imported from ${m.source?.system ?? 'a source system'}, ${m.source?.repository ?? ''}@${m.source?.revision ?? ''}${m.validatedBy?.expert ? `; validated by ${m.validatedBy.expert}` : '; not yet validated by an expert'}`;
  const saved: string[] = [];
  const skipped: string[] = [];
  const draftKeys = new Map<string, string[]>(); // tenant -> the store keys of the drafts saved
  const actorFor = (tenant: string) => (tenant === opts.bankTenant ? opts.bankActor ?? opts.actor : opts.actor);
  const scopeFor = (doc: any) => (doc.kind === 'configuration-set' ? 'b' : doc.name ? parseName(doc.name)?.scope : doc.scope);
  // Every permission first: an import is saved whole, or not at all.
  for (const { doc } of [...b.docs, ...b.tests, ...b.configuration]) {
    const tenant = tenantFor(doc, opts);
    gov.enforce(env, actorFor(tenant), tenant, 'author', keyOf(doc), scopeFor(doc));
  }
  for (const s of b.samples) gov.enforce(env, opts.actor, opts.tenant, 'author', `sample:${s.name}`);
  const save = (doc: any, item?: Item) => {
    if (item?.status === 'unchanged') { skipped.push(`${item.key} (the same as ${item.existing})`); return; }
    const tenant = tenantFor(doc, opts);
    const actor = actorFor(tenant);
    const withProv = doc.kind === 'test' || doc.kind === 'configuration-set' || doc.kind === 'scheme' ? doc : { ...doc, provenance: { origin: 'authored', status: 'provisional', ...(doc.provenance ?? {}), note: [doc.provenance?.note, note].filter(Boolean).join(' ') } };
    const e = env.store.saveDraft(tenant, actor, withProv);
    draftKeys.set(tenant, [...(draftKeys.get(tenant) ?? []), e.key]);
    saved.push(`${tenant}/${keyOf(doc)}`);
  };
  const itemFor = (doc: any) => report.items.find((i) => i.key === keyOf(doc));
  for (const { doc } of b.docs) save(doc, itemFor(doc));
  for (const s of b.scripts) {
    const tenant = tenantFor(docsByNameOf(b, s.activity), opts) || opts.tenant;
    saveScript(env, { tenant, actor: actorFor(tenant), activity: s.activity, language: s.language, source: s.source });
    saved.push(`script ${s.activity}`);
  }
  for (const s of b.samples) {
    saveSample(env, { tenant: opts.tenant, actor: opts.actor, name: s.name, content: s.content });
    saved.push(`samples/${s.name}`);
  }
  for (const { doc } of b.tests) save(doc, itemFor(doc));
  for (const { doc } of b.configuration) save(doc, itemFor(doc));
  env.store.record({ tenant: opts.tenant, actor: opts.actor, action: 'import', key: `extraction:${m.scope}`, detail: `${saved.length} saved as drafts, ${skipped.length} unchanged; ${note}` });
  // The change sets a checker approves: each closure of drafts that refer to one another.
  const changeSets: string[][] = [];
  const done = new Set<string>();
  for (const [tenant, keys] of draftKeys) {
    for (const d of env.store.drafts(tenant)) {
      if (done.has(d.key) || !keys.includes(d.key)) continue;
      try {
        const set = gov.changeSetFor(env, tenant, d.key);
        set.forEach((k) => done.add(k));
        changeSets.push(set);
      } catch { /* a draft that is not part of a set */ }
    }
  }
  return { applied: true, report, saved, skipped, changeSets };
}

function docsByNameOf(b: Bundle, name: string) {
  return b.docs.find((d) => d.doc?.name === name)?.doc;
}
