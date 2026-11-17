// Set the business date from the UAE calendar. The calendar may name the date; otherwise the
// payment runs on the day it was received.

export async function run({ ctx, ports, clock }) {
  const reply = await ports.request('uae.p.port.calendar.v1');
  const received = ctx.header?.receivedAt ? String(ctx.header.receivedAt).slice(0, 10) : clock.today();
  ctx.header = { ...(ctx.header ?? {}), businessDate: reply?.data?.businessDate ?? received };
  return 'set';
}
