// Scheme status request (ISO 20022 pacs.028) about a payment we hold: the original ids.

export async function run({ ctx }) {
  const inbound = ctx.messages?.inbound;
  const xml: string = inbound?.payload ?? '';
  if (!xml || inbound?.malformed) return { outcome: 'unmappable', reasonCode: 'FF01' };
  const m = /<OrgnlEndToEndId>([^<]+)<\/OrgnlEndToEndId>/.exec(xml);
  if (!m) return { outcome: 'unmappable', reasonCode: 'FF01' };
  ctx.header = { endToEndId: m[1].trim() };
  return 'mapped';
}
