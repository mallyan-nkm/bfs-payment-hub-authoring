import * as fs from 'node:fs';
import * as path from 'node:path';
import { parseYamlAll } from './yaml.js';
import { contentHash, clone, canonicalJson } from './canonical.js';
import { createHash } from 'node:crypto';
import { compareReleases, parseName, parseRelease, isShared } from './names.js';

// The artefact store (C01). Artefacts are files in the spec repository (factory, examples,
// conformance) plus the studio's own workspace (drafts and releases published in the studio).
// A published release is immutable: a second write to the same name and release is refused.

export const OWNER_TENANT = 'owner';

/** Documents in a bank's folder that the runtime reads, not the studio: port bindings and configuration values. */
export const RUNTIME_KINDS = ['port-bindings', 'configuration-values'];

export type Status = 'published' | 'draft' | 'in-review' | 'retired';

export interface Entry {
  key: string; // name@release, or id for tests, or scope for schemes and packs
  kind: string;
  name: string;
  release: string;
  scope: string;
  tenant: string;
  status: Status;
  doc: any;
  hash: string;
  path: string; // where it came from, relative to its root
  source: 'spec' | 'studio';
  generated: boolean;
  maker?: string;
  checker?: string;
  publishedAt?: string;
}

export interface AuditEvent {
  at: string;
  tenant: string;
  actor: string;
  action: string;
  key: string;
  detail?: string;
  outcome?: 'done' | 'refused';
  diff?: any;
  prevHash?: string;
  hash?: string; // each record hashes the one before it, so the log cannot be changed unseen (C17-S4)
}

export class StoreError extends Error {
  constructor(public code: string, message: string) {
    super(message);
  }
}

function scopeOfDoc(doc: any): string {
  if (typeof doc.scope === 'string') return doc.scope;
  if (doc.kind === 'supersession' && doc.artefact) return parseName(doc.artefact)?.scope ?? 'p';
  const n = doc.name ? parseName(doc.name) : null;
  if (n) return n.scope;
  if (doc.kind === 'test') {
    const t = doc.target ? parseName(doc.target) : null;
    return t?.scope ?? 'p';
  }
  if (doc.kind === 'configuration-set') return 'b';
  return 'p';
}

function nameOfDoc(doc: any): string {
  if (doc.kind === 'scheme') return `scheme:${doc.scope}`;
  if (doc.kind === 'pack') return `pack:${doc.scope}${doc.tenant ? ':' + doc.tenant : ''}`;
  if (doc.kind === 'inventory') return 'inventory';
  if (doc.kind === 'conformance-kit') return 'conformance-kit';
  return doc.name ?? doc.id ?? (doc.kind === 'configuration-set' ? `config:${doc.tenant}` : doc.kind === 'engine-binding' ? `binding:${doc.target}` : doc.scope ?? doc.kind);
}

/** A bank's own content is keyed by tenant, so two banks may use one name (C17-S1). */
export function tenantKey(doc: any, tenant: string): string {
  return (tenant && tenant !== OWNER_TENANT ? `${tenant}/` : '') + keyOf(doc);
}

export function keyOf(doc: any): string {
  const name = nameOfDoc(doc);
  return doc.release ? `${name}@${doc.release}` : name;
}

export class Store {
  entries = new Map<string, Entry>(); // key -> entry
  extra: Record<string, any> = {}; // persisted studio state: configuration edits, waivers, in-flight imports, users
  audit: AuditEvent[] = [];
  readonly specRoot: string;
  readonly dataRoot: string;

  constructor(specRoot: string, dataRoot: string) {
    this.specRoot = path.resolve(specRoot);
    this.dataRoot = path.resolve(dataRoot);
  }

  static open(specRoot: string, dataRoot: string, extraRoots: string[] = [], bankPacks: string[] = []): Store {
    const s = new Store(specRoot, dataRoot);
    for (const base of ['factory', 'examples', 'conformance']) s.loadDir(path.join(s.specRoot, base), 'spec');
    for (const r of extraRoots) s.loadDir(r, 'spec');
    for (const b of bankPacks) s.loadBankPack(b);
    s.loadWorkspace();
    return s;
  }

  /**
   * A bank pack in its own folder, as a bank's configuration repository holds it
   * (`content-configurations/banks/<bank>/`, Issue I-43). Its `pack.yaml` names the tenant, and every
   * document in the folder is that tenant's. The runtime's own formats there (port bindings and
   * configuration values) are not studio artefacts and stay out of the store.
   */
  loadBankPack(dir: string): string {
    const packFile = path.join(dir, 'pack.yaml');
    if (!fs.existsSync(packFile)) throw new StoreError('bank-pack', `${dir} has no pack.yaml`);
    const pack = parseYamlAll(fs.readFileSync(packFile, 'utf8'))[0];
    if (pack?.kind !== 'pack' || pack.packType !== 'bank' || pack.scope !== 'b' || typeof pack.tenant !== 'string' || !pack.tenant || pack.tenant === OWNER_TENANT) {
      throw new StoreError('bank-pack', `${packFile} is not a bank pack: it needs kind pack, packType bank, scope b and a tenant`);
    }
    for (const f of walkYaml(dir)) {
      const text = fs.readFileSync(f, 'utf8');
      for (const doc of parseYamlAll(text)) {
        if (!doc || typeof doc !== 'object' || RUNTIME_KINDS.includes(doc.kind)) continue;
        if (doc.tenant && doc.tenant !== pack.tenant) throw new StoreError('bank-pack', `${f} names tenant ${doc.tenant}, but its pack is ${pack.tenant}'s`);
        this.add(doc, path.relative(this.specRoot, f), 'spec', text.startsWith('# Generated by'), 'published', pack.tenant);
      }
    }
    return pack.tenant;
  }

  /** A store over plain folders of YAML, as a deployed worker holds its artefacts. */
  static fromDirs(dirs: string[]): Store {
    const s = new Store(dirs[0] ?? '.', '');
    for (const d of dirs) s.loadDir(d, 'spec');
    return s;
  }

  private loadDir(dir: string, source: 'spec' | 'studio') {
    if (!fs.existsSync(dir)) return;
    for (const f of walkYaml(dir)) {
      const text = fs.readFileSync(f, 'utf8');
      const generated = text.startsWith('# Generated by');
      for (const doc of parseYamlAll(text)) {
        if (!doc || typeof doc !== 'object') continue;
        this.add(doc, path.relative(this.specRoot, f), source, generated, 'published');
      }
    }
  }

  private tenantOfDoc(doc: any, file: string): string {
    if (doc.tenant) return doc.tenant;
    if (file.includes(`examples${path.sep}bank`)) return 'example-bank';
    return OWNER_TENANT;
  }

  add(doc: any, file: string, source: 'spec' | 'studio', generated: boolean, status: Status, tenant?: string): Entry {
    const name = nameOfDoc(doc);
    const e: Entry = {
      key: tenantKey(doc, tenant ?? this.tenantOfDoc(doc, file)) + (status === 'draft' ? '#draft' : ''),
      kind: doc.kind,
      name,
      release: doc.release ?? '',
      scope: scopeOfDoc(doc),
      tenant: tenant ?? this.tenantOfDoc(doc, file),
      status,
      doc,
      hash: contentHash(doc),
      path: file,
      source,
      generated,
    };
    this.entries.set(e.key, e);
    return e;
  }

  // ---------------------------------------------------------------- queries

  all(): Entry[] {
    return [...this.entries.values()];
  }

  /** What a tenant may see: shared content and its own (R-06-01). */
  visible(tenant: string): Entry[] {
    return this.all().filter((e) => e.tenant === OWNER_TENANT || e.tenant === tenant || (tenant === OWNER_TENANT && false));
  }

  tenants(): string[] {
    const t = new Set<string>([OWNER_TENANT]);
    for (const e of this.all()) t.add(e.tenant);
    return [...t];
  }

  releasesOf(name: string, opts: { includeDrafts?: boolean; tenant?: string } = {}): Entry[] {
    return this.all()
      .filter((e) => e.name === name && (opts.includeDrafts || e.status === 'published'))
      .filter((e) => !opts.tenant || e.tenant === OWNER_TENANT || e.tenant === opts.tenant)
      .sort((a, b) => compareReleases(a.release, b.release));
  }

  /** The latest published release of a major line. */
  latest(name: string, tenant?: string): Entry | undefined {
    const r = this.releasesOf(name, { tenant });
    return r[r.length - 1];
  }

  get(name: string, release?: string, tenant?: string): Entry | undefined {
    // A pack manifest names its scheme manifest `<scope>.scheme`.
    if (name.endsWith('.scheme') && !this.entries.has(`${name}@${release}`)) return this.get(`scheme:${name.slice(0, -'.scheme'.length)}`, release, tenant);
    if (release) {
      const own = this.entries.get(`${name}@${release}`);
      if (own) return own;
      if (tenant) return this.entries.get(`${tenant}/${name}@${release}`);
      return this.all().find((e) => e.name === name && e.release === release && (e.status === 'published' || e.status === 'retired'));
    }
    return this.latest(name, tenant) ?? this.entries.get(name);
  }

  doc(name: string, release?: string, tenant?: string): any {
    return this.get(name, release, tenant)?.doc;
  }

  byKind(kind: string): Entry[] {
    return this.all().filter((e) => e.kind === kind && e.status === 'published');
  }

  test(id: string): Entry | undefined {
    return this.byKind('test').find((e) => e.name === id);
  }

  scheme(scope: string): any {
    return this.byKind('scheme').find((e) => e.scope === scope)?.doc;
  }

  configurationSet(tenant: string): any {
    const sets = this.byKind('configuration-set').filter((e) => e.doc.tenant === tenant);
    sets.sort((a, b) => compareReleases(a.release, b.release));
    return sets[sets.length - 1]?.doc;
  }

  // -------------------------------------------------------------- authoring

  /** Who may author in a scope (R-06-03): a bank tenant only in `b`; the owner only in shared scopes. */
  assertMayAuthor(tenant: string, doc: any) {
    const scope = scopeOfDoc(doc);
    if (tenant !== OWNER_TENANT && isShared(scope) && doc.kind !== 'configuration-set') {
      throw new StoreError('R-06-03', `Tenant ${tenant} may not create or change ${keyOf(doc)}: shared content is read-only to a bank`);
    }
    if (tenant === OWNER_TENANT && !isShared(scope)) {
      throw new StoreError('R-06-03', `The owner tenant does not author bank content (${keyOf(doc)})`);
    }
  }

  saveDraft(tenant: string, actor: string, doc: any): Entry {
    this.assertMayAuthor(tenant, doc);
    // A test case is keyed by its id and carries no release (test-case.schema.json).
    if (doc.kind !== 'test' && (!doc.release || !parseRelease(doc.release))) throw new StoreError('R-01-08', 'A draft needs a release major.minor.patch');
    const published = this.entries.get(tenantKey(doc, tenant));
    if (published) throw new StoreError('R-01-11', `${keyOf(doc)} is published and immutable. Release a new one.`);
    const e = this.add(clone(doc), `studio:${tenant}`, 'studio', false, 'draft', tenant);
    this.record({ tenant, actor, action: 'save-draft', key: e.key });
    this.persist();
    return e;
  }

  drafts(tenant: string): Entry[] {
    return this.all().filter((e) => (e.status === 'draft' || e.status === 'in-review') && e.tenant === tenant);
  }

  deleteDraft(tenant: string, actor: string, key: string) {
    const e = this.entries.get(key);
    if (!e || e.status !== 'draft' || e.tenant !== tenant) throw new StoreError('not-found', `No draft ${key} in ${tenant}`);
    this.entries.delete(key);
    this.record({ tenant, actor, action: 'delete-draft', key });
    this.persist();
  }

  /** Publish a draft through maker-checker (R-06-04). The maker is the actor who saved it. */
  publish(tenant: string, draftKey: string, maker: string, checker: string): Entry {
    const d = this.entries.get(draftKey);
    if (!d || (d.status !== 'draft' && d.status !== 'in-review') || d.tenant !== tenant) throw new StoreError('not-found', `No draft ${draftKey} in ${tenant}`);
    if (!maker || !checker || maker === checker) {
      this.record({ tenant, actor: checker || maker, action: 'publish', key: draftKey, outcome: 'refused', detail: 'Maker and checker must differ (R-06-04)' });
      throw new StoreError('R-06-04', 'Maker and checker must differ');
    }
    const key = tenantKey(d.doc, tenant);
    if (this.entries.has(key)) throw new StoreError('R-01-11', `${key} is already published. A published release is immutable.`);
    this.entries.delete(draftKey);
    const e = this.add(d.doc, `studio:${tenant}`, 'studio', false, 'published', tenant);
    e.maker = maker;
    e.checker = checker;
    e.publishedAt = new Date().toISOString();
    const prev = this.releasesOf(e.name).filter((x) => x.key !== e.key).pop();
    this.record({ tenant, actor: checker, action: 'publish', key, detail: `maker ${maker}, checker ${checker}`, diff: prev ? leafDiff(prev.doc, e.doc) : { created: true } });
    this.persist();
    return e;
  }

  record(ev: Omit<AuditEvent, 'at' | 'hash' | 'prevHash'>) {
    const prevHash = this.audit[this.audit.length - 1]?.hash ?? 'sha256:' + '0'.repeat(64);
    const body = { at: new Date().toISOString(), outcome: 'done' as const, ...ev, prevHash };
    const hash = 'sha256:' + createHash('sha256').update(canonicalJson(body), 'utf8').digest('hex');
    this.audit.push({ ...body, hash });
    this.persist();
  }

  /** Verify the audit chain: every record hashes the one before it. */
  verifyAudit(): { ok: boolean; brokenAt?: number } {
    let prev = 'sha256:' + '0'.repeat(64);
    for (let i = 0; i < this.audit.length; i++) {
      const { hash, ...body } = this.audit[i];
      if (body.prevHash !== prev) return { ok: false, brokenAt: i };
      const h = 'sha256:' + createHash('sha256').update(canonicalJson(body), 'utf8').digest('hex');
      if (h !== hash) return { ok: false, brokenAt: i };
      prev = hash!;
    }
    return { ok: true };
  }

  /** Move a draft to review, or a review back to draft (C19-S2). */
  setState(key: string, status: Status) {
    const e = this.entries.get(key);
    if (!e) throw new StoreError('not-found', `No ${key}`);
    e.status = status;
    this.persist();
    return e;
  }

  /** Retire a published release (C19-S3). The caller checks that nothing pins it. */
  retire(name: string, release: string, actor: string, tenant: string) {
    const e = this.get(name, release, tenant);
    if (!e || e.status !== 'published') throw new StoreError('not-found', `${name}@${release} is not published`);
    e.status = 'retired';
    if (e.source === 'spec') this.retiredSpec.add(e.key);
    this.record({ tenant, actor, action: 'retire', key: e.key });
    return e;
  }

  retiredSpec = new Set<string>();

  // ------------------------------------------------------------ persistence

  persist() {
    if (!this.dataRoot || this.dataRoot === path.resolve('')) return;
    fs.mkdirSync(this.dataRoot, { recursive: true });
    const studio = this.all().filter((e) => e.source === 'studio');
    const out = studio.map((e) => ({ tenant: e.tenant, status: e.status, doc: e.doc, maker: e.maker, checker: e.checker, publishedAt: e.publishedAt }));
    fs.writeFileSync(path.join(this.dataRoot, 'workspace.json'), JSON.stringify({ entries: out, audit: this.audit, retired: [...this.retiredSpec], extra: this.extra }, null, 2));
  }

  private loadWorkspace() {
    const f = path.join(this.dataRoot, 'workspace.json');
    if (!fs.existsSync(f)) return;
    const w = JSON.parse(fs.readFileSync(f, 'utf8'));
    for (const x of w.entries ?? []) {
      const e = this.add(x.doc, `studio:${x.tenant}`, 'studio', false, x.status, x.tenant);
      e.maker = x.maker;
      e.checker = x.checker;
      e.publishedAt = x.publishedAt;
    }
    this.audit = w.audit ?? [];
    this.extra = w.extra ?? {};
    for (const k of w.retired ?? []) {
      const e = this.entries.get(k);
      if (e) e.status = 'retired';
      this.retiredSpec.add(k);
    }
  }
}

export function walkYaml(dir: string): string[] {
  const out: string[] = [];
  for (const ent of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) out.push(...walkYaml(p));
    else if (ent.name.endsWith('.yaml') || ent.name.endsWith('.yml')) out.push(p);
  }
  return out;
}

function leafDiff(a: any, b: any, prefix = '', out: Record<string, { before: any; after: any }> = {}) {
  const keys = new Set([...Object.keys(a ?? {}), ...Object.keys(b ?? {})]);
  for (const k of keys) {
    const p = prefix ? `${prefix}.${k}` : k;
    const x = a?.[k];
    const y = b?.[k];
    if (JSON.stringify(x) === JSON.stringify(y)) continue;
    if (x && y && typeof x === 'object' && typeof y === 'object' && !Array.isArray(x) && !Array.isArray(y)) leafDiff(x, y, p, out);
    else out[p] = { before: x, after: y };
  }
  return out;
}
