import * as fs from 'node:fs';
import * as path from 'node:path';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

// Schema validation by kind (C02). The schemas in spec/schemas are the source of truth.

export const SCHEMA_OF: Record<string, string> = {
  activity: 'activity', rule: 'rule', ruleset: 'rule', table: 'rule', workflow: 'workflow',
  simulator: 'simulator', scheme: 'scheme', hook: 'hook', 'configuration-set': 'configuration-set',
  pack: 'pack', 'engine-binding': 'engine-binding', test: 'test-case', extension: 'extension',
  port: 'port', supersession: 'supersession', 'operations-view': 'operations-view', 'error-dictionary': 'error-dictionary',
  'port-bindings': 'port-bindings', 'reference-table': 'reference-table',
};

export interface SchemaIssue {
  code: 'L00';
  path: string; // JSON pointer into the document
  message: string;
  hint?: string;
}

export class SchemaValidator {
  private ajv: any;
  readonly ids: string[] = [];

  constructor(schemaDir: string) {
    const AjvCtor: any = (Ajv2020 as any).default ?? Ajv2020;
    this.ajv = new AjvCtor({ allErrors: true, strict: false });
    const fmt: any = (addFormats as any).default ?? addFormats;
    fmt(this.ajv);
    for (const f of fs.readdirSync(schemaDir).filter((f) => f.endsWith('.schema.json')).sort()) {
      const s = JSON.parse(fs.readFileSync(path.join(schemaDir, f), 'utf8'));
      this.ajv.addSchema(s);
      this.ids.push(s.$id);
    }
  }

  validate(doc: any): SchemaIssue[] {
    const kind = doc?.kind;
    if (kind === 'inventory' || kind === 'conformance-kit') return [];
    const schema = SCHEMA_OF[kind];
    if (!schema) return [{ code: 'L00', path: '/kind', message: `Unknown kind ${JSON.stringify(kind)}`, hint: `Use one of: ${Object.keys(SCHEMA_OF).join(', ')}` }];
    const v = this.ajv.getSchema(`urn:th:schema:${schema}`);
    if (!v) return [{ code: 'L00', path: '', message: `No schema for ${schema}` }];
    if (v(doc)) return [];
    return humanise(v.errors ?? []);
  }
}

/** Errors a designer can act on (C02-S2): keep the most specific error per location, in plain words. */
function humanise(errors: any[]): SchemaIssue[] {
  const byPath = new Map<string, any[]>();
  for (const e of errors) {
    const p = e.instancePath || '';
    if (!byPath.has(p)) byPath.set(p, []);
    byPath.get(p)!.push(e);
  }
  const out: SchemaIssue[] = [];
  // Drop parent locations when a child location has errors: the child is more specific.
  const paths = [...byPath.keys()];
  for (const p of paths) {
    if (paths.some((q) => q !== p && q.startsWith(p + '/')) && byPath.get(p)!.every((e) => ['oneOf', 'anyOf', 'if'].includes(e.keyword))) continue;
    const errs = byPath.get(p)!;
    const specific = errs.filter((e) => !['oneOf', 'anyOf', 'if', 'allOf'].includes(e.keyword));
    for (const e of (specific.length ? specific : errs).slice(0, 3)) out.push(toIssue(p, e));
  }
  return dedupe(out);
}

function toIssue(p: string, e: any): SchemaIssue {
  const where = p || '(document)';
  switch (e.keyword) {
    case 'required':
      return { code: 'L00', path: p, message: `${where} is missing ${e.params.missingProperty}`, hint: `Add ${e.params.missingProperty}.` };
    case 'additionalProperties':
      return { code: 'L00', path: `${p}/${e.params.additionalProperty}`, message: `${where} does not allow ${e.params.additionalProperty}`, hint: e.params.additionalProperty === 'on' || e.params.additionalProperty === 'yes' || e.params.additionalProperty === 'no' || e.params.additionalProperty === 'off' ? 'YAML reads on, off, yes and no as booleans. Use handle on a call or slot and by on a switch.' : 'Remove it, or check the spelling.' };
    case 'enum':
      return { code: 'L00', path: p, message: `${where} must be one of ${e.params.allowedValues.join(', ')}` };
    case 'pattern':
      return { code: 'L00', path: p, message: `${where} does not match ${e.params.pattern}`, hint: p.endsWith('/name') ? 'A name is <scope>.<type>.<name>.v<major>.' : undefined };
    case 'type':
      return { code: 'L00', path: p, message: `${where} must be ${e.params.type}` };
    case 'const':
      return { code: 'L00', path: p, message: `${where} must be ${JSON.stringify(e.params.allowedValue)}` };
    default:
      return { code: 'L00', path: p, message: `${where} ${e.message}` };
  }
}

function dedupe(xs: SchemaIssue[]): SchemaIssue[] {
  const seen = new Set<string>();
  return xs.filter((x) => (seen.has(x.path + x.message) ? false : (seen.add(x.path + x.message), true)));
}
