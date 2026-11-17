import type { Facts } from '../rules/service.js';
import { coveredByAny, getPath } from '../core/paths.js';

// Facts for a rule session (Part 4, 4.3): built from the calling activity's declared reads.
// An extension contributes one fact type per level, named after its scope: `ext.uae.wps`
// gives `WpsBatch` and `WpsItem`.

export function extensionFactPrefix(root: string): string {
  const parts = root.split('.');
  const last = parts[parts.length - 1];
  return last.charAt(0).toUpperCase() + last.slice(1);
}

export interface FactInput {
  ctx: any; // the activity's view of the context
  reads: string[];
  level?: 'batch' | 'item';
  extensions: any[]; // extension documents
  reference?: any[];
  sourceFile?: { name: string; size: number };
}

export function buildFacts(inp: FactInput): Facts {
  const { ctx, reads } = inp;
  const facts: Facts = {};
  const can = (p: string) => coveredByAny(reads, p) || reads.some((r) => r.startsWith(p + '.')) || reads.some((r) => r.replace('[]', '') === p.replace('[]', ''));
  if (ctx.header && can('header')) facts.Header = [ctx.header];
  if (ctx.payment && (can('payment') || reads.some((r) => r.startsWith('payment')))) facts.Payment = [ctx.payment];
  if (ctx.status && can('status')) facts.Status = [ctx.status];
  if (ctx.facts && (can('facts') || reads.some((r) => r.startsWith('facts')))) facts.Facts = [ctx.facts];
  const batchId = ctx.batch?.id ?? ctx.header?.fileId;
  if (ctx.batch && (can('batch') || reads.some((r) => r.startsWith('batch')))) facts.Batch = [{ ...ctx.batch, id: batchId }];
  const items: any[] = Array.isArray(ctx.items) ? ctx.items : [];
  if (items.length && reads.some((r) => r.startsWith('items'))) {
    facts.Item = items.map((it) => ({ ...flattenExt(it.ext), ...it, batchId }));
  }
  for (const ext of inp.extensions) {
    const root: string = ext.root; // e.g. ext.uae.wps
    const prefix = extensionFactPrefix(root);
    const rel = root.replace(/^ext\./, '');
    const batchVal = getPath(ctx.ext ?? {}, rel);
    if (batchVal && (coveredByAny(reads, root) || reads.some((r) => r.startsWith('ext')))) facts[`${prefix}Batch`] = [batchVal];
    if (items.length && reads.some((r) => r.startsWith('items'))) {
      const xs = items.map((it) => getPath(it.ext ?? {}, rel)).filter((x) => x !== undefined);
      if (xs.length) facts[`${prefix}Item`] = xs;
    }
  }
  if (inp.level === 'item') {
    // At item level the payment is the item's payment (R-05-28); the item's own ext is its fact.
    if (ctx.payment) facts.Payment = [ctx.payment];
  }
  if (inp.reference?.length) facts.Reference = inp.reference;
  if (inp.sourceFile) facts.SourceFile = [inp.sourceFile];
  return facts;
}

function flattenExt(ext: any): any {
  const out: any = {};
  const walk = (o: any) => {
    if (!o || typeof o !== 'object') return;
    for (const [k, v] of Object.entries(o)) {
      if (v && typeof v === 'object' && !Array.isArray(v)) walk(v);
      else out[k] = v;
    }
  };
  walk(ext);
  return out;
}
