// Is core banking taking postings now? The bank's window (windowStart to windowEnd, HH:mm, business-local, both inclusive;
// an end before the start is an overnight window), on the engine's clock, at UTC+4 (the UAE has no daylight saving).
// The same rule as the runtime's native code-behind (PostingWindow, the source's CbCallWindowGate, R-08-16).
// An invalid window is closed.

const HHMM = /^([01][0-9]|2[0-3]):([0-5][0-9])$/;
const minutes = (s: string) => {
  const m = HHMM.exec(String(s ?? '').trim());
  return m ? Number(m[1]) * 60 + Number(m[2]) : undefined;
};

export async function run({ ctx, params, clock }) {
  const now = new Date(Date.parse(clock.now()) + 4 * 3600000);
  const at = now.getUTCHours() * 60 + now.getUTCMinutes();
  const start = minutes(params.windowStart);
  const end = minutes(params.windowEnd);
  const hh = (n: number) => String(n).padStart(2, '0');
  let open = false;
  if (start !== undefined && end !== undefined) open = start <= end ? at >= start && at <= end : at >= start || at <= end;
  const state = open ? 'open' : 'closed';
  ctx.facts = { ...(ctx.facts ?? {}), postingWindow: { outcome: state, result: state, at: `${hh(Math.floor(at / 60))}:${hh(at % 60)}`, ...(start === undefined || end === undefined ? { invalid: true } : {}) } };
  return state;
}
