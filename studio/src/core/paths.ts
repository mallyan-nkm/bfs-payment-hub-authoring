// Context paths (Part 2). A path starts with one of the seven parts and uses dots.
// `items[]` addresses the item list; `items[].x` addresses x in every item.

export const CONTEXT_PARTS = ['header', 'payment', 'batch', 'items[]', 'status', 'facts', 'ext', 'messages'];

export interface Context {
  header?: Record<string, any>;
  payment?: Record<string, any>;
  batch?: Record<string, any>;
  items?: Array<Record<string, any>>;
  status?: Record<string, any>;
  facts?: Record<string, any>;
  ext?: Record<string, any>;
  messages?: Record<string, any>;
  [k: string]: any;
}

function segs(path: string): string[] {
  return path.replace(/^items\[\]/, 'items').split('.');
}

export function getPath(obj: any, path: string): any {
  let cur = obj;
  for (const s of segs(path)) {
    if (cur === null || cur === undefined) return undefined;
    cur = cur[s];
  }
  return cur;
}

export function setPath(obj: any, path: string, value: any): void {
  const parts = segs(path);
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i++) {
    if (cur[parts[i]] === undefined || cur[parts[i]] === null || typeof cur[parts[i]] !== 'object') cur[parts[i]] = {};
    cur = cur[parts[i]];
  }
  cur[parts[parts.length - 1]] = value;
}

/** Is `path` equal to or under `root`? `items[]` covers `items[].x`. */
export function covers(root: string, path: string): boolean {
  const r = root.replace(/\[\]/g, '');
  const p = path.replace(/\[\]/g, '');
  return p === r || p.startsWith(r + '.');
}

export function coveredByAny(roots: string[], path: string): boolean {
  return roots.some((r) => covers(r, path));
}

/** Flatten an object into leaf paths. Arrays of objects are flattened with an index. */
export function leaves(obj: any, prefix = ''): Record<string, any> {
  const out: Record<string, any> = {};
  if (obj === null || typeof obj !== 'object') {
    if (prefix) out[prefix] = obj;
    return out;
  }
  if (Array.isArray(obj)) {
    if (obj.length === 0 || obj.every((x) => x === null || typeof x !== 'object')) {
      out[prefix] = obj;
      return out;
    }
    obj.forEach((v, i) => Object.assign(out, leaves(v, `${prefix}[${i}]`)));
    return out;
  }
  const keys = Object.keys(obj);
  if (keys.length === 0 && prefix) out[prefix] = {};
  for (const k of keys) Object.assign(out, leaves(obj[k], prefix ? `${prefix}.${k}` : k));
  return out;
}

export interface DeltaEntry { path: string; before: any; after: any }

/** The writes of a step as a delta against the context (R-02-10). */
export function diff(before: any, after: any): DeltaEntry[] {
  const a = leaves(before);
  const b = leaves(after);
  const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
  const out: DeltaEntry[] = [];
  for (const k of [...keys].sort()) {
    if (JSON.stringify(a[k]) !== JSON.stringify(b[k])) out.push({ path: k, before: a[k], after: b[k] });
  }
  return out;
}

/** Pick the declared paths out of a context, for a read view. */
export function pick(ctx: any, paths: string[]): any {
  const out: any = {};
  for (const p of paths) {
    const v = getPath(ctx, p);
    if (v !== undefined) setPath(out, p, JSON.parse(JSON.stringify(v)));
  }
  return out;
}
