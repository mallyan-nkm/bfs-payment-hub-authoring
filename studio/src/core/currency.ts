// The country's default currency (R-02-24; owner's decision of 24 November 2026). A country cluster declares it: the parameter its
// platform scope (`<cluster>.p`) marks `role: default-currency`, an ISO 4217 code with bounds (lint L56), AED for the UAE
// (uae.p.enrich.default-currency.v1#currency). A bank of the cluster inherits it and may set its own value of that parameter. The
// platform `p` names no currency (R-09-185): the front door gives the default, after the intake map, to a payment whose file, lines
// and payment name none, and a bank's configuration release gives it to a core-banking binding that names none. The canonical checks
// still refuse a payment that ends with no currency.

export const DEFAULT_CURRENCY_ROLE = 'default-currency';
export const CORE_BANKING_PORT = 'p.port.core-banking.v1';

export interface DefaultCurrencyDecl { owner: string; parameter: string; decl: any; cluster: string }

/** The cluster of a scope: `uae` for `uae.wps` or `uae.p`; none for the platform `p` or a bank's `b`. */
export function clusterOf(scope: string | undefined): string | undefined {
  const first = String(scope ?? '').split('.')[0];
  return first && first !== 'p' && first !== 'b' ? first : undefined;
}

/** The cluster's declaration of its default currency among the documents, or none. */
export function defaultCurrencyDecl(docs: any[], cluster: string | undefined): DefaultCurrencyDecl | undefined {
  if (!cluster) return undefined;
  for (const d of docs) {
    if (!d?.name || !String(d.name).startsWith(`${cluster}.p.`)) continue;
    for (const [k, decl] of Object.entries<any>(d.parameters ?? {}))
      if (decl?.role === DEFAULT_CURRENCY_ROLE) return { owner: d.name, parameter: k, decl, cluster };
  }
  return undefined;
}

/** Whether a context names a currency: its file's batch, one of its lines, or its payment. */
export function namesCurrency(ctx: any): boolean {
  const items: any[] = Array.isArray(ctx?.items) ? ctx.items : [];
  return Boolean(ctx?.batch?.currency) || Boolean(ctx?.payment?.instructedAmount?.currency) || items.some((i) => Boolean(i?.payment?.instructedAmount?.currency));
}

/** Gives a context that names no currency the default: its batch's where it is a file, else its payment's. Says whether it did. */
export function applyDefaultCurrency(ctx: any, currency: unknown): boolean {
  if (namesCurrency(ctx) || typeof currency !== 'string' || !/^[A-Z]{3}$/.test(currency)) return false;
  if (ctx.batch) ctx.batch = { ...ctx.batch, currency };
  else ctx.payment = { ...(ctx.payment ?? {}), instructedAmount: { ...(ctx.payment?.instructedAmount ?? {}), currency } };
  return true;
}

/** The bank's value of the default currency on a date: its own, else the cluster's declared default. */
export function bankDefaultCurrency(found: DefaultCurrencyDecl | undefined, values: any[], date: string): string | undefined {
  if (!found) return undefined;
  const key = `${found.owner}#${found.parameter}`;
  const inForce = values.filter((v) => v?.parameter === key && String(v.validFrom ?? '') <= date && (!v.validTo || v.validTo >= date))
    .sort((a, b) => String(a.validFrom).localeCompare(String(b.validFrom)));
  const v = inForce.length ? inForce[inForce.length - 1].value : found.decl.default;
  return typeof v === 'string' && /^[A-Z]{3}$/.test(v) ? v : undefined;
}

/** A bank's bindings with the default currency given to each core-banking binding that names none (R-02-24). */
export function withDefaultCurrency(bindings: any[], currency: string | undefined): any[] {
  if (!currency) return bindings;
  return bindings.map((b) => {
    if (b?.port !== CORE_BANKING_PORT) return b;
    // A file bulk's local currency is its own setting (localCurrency); every other adapter's is currency.
    const key = b.adapter === 'file-bulk' ? 'localCurrency' : 'currency';
    return b.settings?.[key] ? b : { ...b, settings: { ...(b.settings ?? {}), [key]: currency } };
  });
}
