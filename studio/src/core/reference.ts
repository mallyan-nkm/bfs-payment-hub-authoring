// Reference tables (R-01-25, R-11-48; owner's decisions of 23 November 2026). Market, scheme and bank reference data as an
// effective-dated table: values, not logic. A table travels to the runtime in a bank's configuration release and is served there
// by GET /lookups/{domain}; a configuration value may read a list from one (`fromReference`). The same checks as
// spec/tools/validate.py (L49, L50).

const TYPES: Record<string, (v: unknown) => boolean> = {
  code: (v) => typeof v === 'string', text: (v) => typeof v === 'string', date: (v) => typeof v === 'string',
  boolean: (v) => typeof v === 'boolean', number: (v) => typeof v === 'number', list: (v) => Array.isArray(v),
};

/** L49: the key and the columns agree, the editions run in date order, every row fits, a key is not repeated in an edition. */
export function referenceTableProblems(d: any, tables: Map<string, any>): string[] {
  const out: string[] = [];
  const cols = new Map<string, any>((d.columns ?? []).map((c: any) => [c.id, c]));
  const key: string[] = d.key ?? [];
  if (cols.size !== (d.columns ?? []).length) out.push('names a column twice');
  for (const k of key) if (!cols.has(k)) out.push(`its key names ${k}, which is not a column`);
  for (const c of cols.values()) if (c.references && (!tables.has(c.references) || c.references === d.name)) out.push(`column ${c.id} references ${c.references}, which is no other reference table`);
  const dates = (d.editions ?? []).map((e: any) => String(e.validFrom));
  if (dates.some((x: string, i: number) => i > 0 && x <= dates[i - 1])) out.push('its editions are not in strictly increasing validFrom order');
  for (const e of d.editions ?? []) {
    const seen = new Set<string>();
    (e.rows ?? []).forEach((row: any, i: number) => {
      const at = `edition ${e.validFrom}, row ${i + 1}`;
      for (const k of Object.keys(row)) if (!cols.has(k)) out.push(`${at} has ${k}, which is not a column`);
      for (const c of cols.values()) {
        const v = row[c.id];
        if (v === undefined || v === null || v === '') {
          if (c.required || key.includes(c.id)) out.push(`${at} has no ${c.id}`);
          continue;
        }
        if (!TYPES[c.type]?.(v)) out.push(`${at}: ${c.id} is not a ${c.type}`);
        else if (c.pattern && typeof v === 'string' && !new RegExp(c.pattern).test(v)) out.push(`${at}: ${c.id} ${JSON.stringify(v)} does not match ${c.pattern}`);
        else if (c.allowed && !c.allowed.includes(v)) out.push(`${at}: ${c.id} ${JSON.stringify(v)} is not one of ${c.allowed.join(', ')}`);
        else if (c.references && tables.has(c.references)) {
          const ref = tables.get(c.references);
          const k0 = (ref.key ?? [])[0];
          const keys = new Set((ref.editions ?? []).flatMap((x: any) => (x.rows ?? []).map((r: any) => String(r[k0]))));
          if (!keys.has(String(v))) out.push(`${at}: ${c.id} ${JSON.stringify(v)} is no key of ${c.references}`);
        }
      }
      const k = key.map((x) => String(row[x])).join('/');
      if (seen.has(k)) out.push(`${at} repeats the key ${k}`);
      seen.add(k);
    });
  }
  return out;
}

/** L50: a value read from a reference table names a published one and its columns, for a list parameter. */
export function referenceValueProblems(v: any, decl: any, table: any): string[] {
  const fr = v.fromReference ?? {};
  if (!table || table.kind !== 'reference-table') return [`${v.parameter} reads ${fr.table}, which is no reference table`];
  const out: string[] = [];
  if (decl?.type !== 'list') out.push(`${v.parameter} reads a reference table, but it is a ${decl?.type}, not a list`);
  const cols = new Set((table.columns ?? []).map((c: any) => c.id));
  for (const col of Object.values<string>(fr.columns ?? {})) if (!cols.has(col)) out.push(`${v.parameter} reads column ${col}, which ${table.name} does not have`);
  return out;
}

/** The items one edition gives a `fromReference` value: a row with an empty column named is left out. */
export function itemsOf(edition: any, columns: Record<string, string>): any[] {
  const out: any[] = [];
  for (const row of edition.rows ?? []) {
    const item: Record<string, unknown> = {};
    let empty = false;
    for (const [field, col] of Object.entries(columns)) {
      const v = row[col];
      if (v === undefined || v === null || v === '') empty = true;
      else item[field] = v;
    }
    if (!empty) out.push(item);
  }
  return out;
}

/**
 * The configuration values with every `fromReference` value read from its table: one value per edition in force from the value's
 * own validFrom on, each starting at the later of the two dates. A value whose table is not found is left out (L50 reports it).
 */
export function resolveReferenceValues(values: any[], tableOf: (name: string) => any): any[] {
  const out: any[] = [];
  for (const v of values ?? []) {
    if (!v?.fromReference) {
      out.push(v);
      continue;
    }
    const t = tableOf(v.fromReference.table);
    if (!t) continue;
    const from = String(v.validFrom);
    const eds = [...(t.editions ?? [])].sort((a: any, b: any) => String(a.validFrom).localeCompare(String(b.validFrom)));
    const first = [...eds].reverse().find((e: any) => String(e.validFrom) <= from);
    const later = eds.filter((e: any) => String(e.validFrom) > from && (!v.validTo || String(e.validFrom) <= String(v.validTo)));
    for (const e of [...(first ? [first] : []), ...later]) {
      const start = String(e.validFrom) > from ? String(e.validFrom) : from;
      const { fromReference, ...rest } = v;
      out.push({ ...rest, value: itemsOf(e, fromReference.columns ?? {}), validFrom: start });
    }
  }
  return out;
}

/** The rows of a table in force at a date: the latest edition whose validFrom is not after it. */
export function rowsAt(table: any, date: string): any[] {
  const eds = [...(table?.editions ?? [])].filter((e: any) => String(e.validFrom) <= date).sort((a: any, b: any) => String(a.validFrom).localeCompare(String(b.validFrom)));
  return eds.length ? eds[eds.length - 1].rows ?? [] : [];
}
