// The bank's cut-off (R-12-28, Issues I-186 and I-191). A file accepted after the cut-off of its scheme and direction counts for
// the next business day, or is refused (TM01, ISO 20022's "received after the cut-off"), as the bank's row says. Every list is the
// bank's value, read from a reference table of its configuration release, edition by edition (fromReference, R-11-51):
//   cutOffs          the bank's table of domain cut-offs       scheme, direction, cutOff (HH:mm local), timeZone (IANA), afterCutOff
//   closingDays      the bank's table of domain bank-calendar  day, kind (closed is no business day)
//   earlyClosings    the bank's table of domain bank-calendar  day, closesAt (a half day: the cut-off is the earlier of the two)
//   nonBusinessDays  the market's business calendar           day (a weekday name or a date); none given: Saturday and Sunday
// The file's moment is header.receivedAt (the front door's acceptance), else the engine's clock. A file accepted on a day that is
// no business day counts for the next business day and is never refused for it (Issue I-191 (3)). Without a row for the file's
// scheme and direction nothing changes.

const HHMM = /^([01]\d|2[0-3]):([0-5]\d)$/;
const WEEKDAYS = ['sunday', 'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday'];

function minutesOf(s: unknown): number | undefined {
  const m = HHMM.exec(String(s ?? '').trim());
  return m ? Number(m[1]) * 60 + Number(m[2]) : undefined;
}

/** The local date and time of an instant in an IANA time zone: { date: YYYY-MM-DD, seconds since local midnight, text }. */
function local(iso: string, timeZone: string) {
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit',
  }).formatToParts(new Date(Date.parse(iso)));
  const p: Record<string, string> = {};
  for (const x of parts) p[x.type] = x.value;
  const date = `${p.year}-${p.month}-${p.day}`;
  return { date, seconds: Number(p.hour) * 3600 + Number(p.minute) * 60 + Number(p.second), text: `${date}T${p.hour}:${p.minute}:${p.second}` };
}

function nextDay(date: string): string {
  const d = new Date(Date.parse(`${date}T00:00:00Z`) + 86400000);
  return d.toISOString().slice(0, 10);
}

export async function run({ ctx, params, clock }) {
  const header = ctx.header ?? {};
  const rows: any[] = Array.isArray(params.cutOffs) ? params.cutOffs : [];
  const row = rows.find((r) => r?.scheme === header.scheme && r?.direction === header.direction);
  const facts = (f: any) => (ctx.facts = { ...(ctx.facts ?? {}), cutOff: f });
  if (!row) {
    facts({ outcome: 'taken', result: 'taken', applied: false, reason: 'no cut-off for the scheme and direction', scheme: header.scheme, direction: header.direction });
    return 'taken';
  }

  const weekend = new Set<number>();
  const holidays = new Set<string>();
  const days: any[] = Array.isArray(params.nonBusinessDays) ? params.nonBusinessDays : [];
  for (const d of days) {
    const day = String(d?.day ?? '').toLowerCase();
    if (WEEKDAYS.includes(day)) weekend.add(WEEKDAYS.indexOf(day));
    else if (/^\d{4}-\d{2}-\d{2}$/.test(day)) holidays.add(day);
  }
  if (!days.length) { weekend.add(0); weekend.add(6); }
  for (const c of Array.isArray(params.closingDays) ? params.closingDays : []) if (c?.kind === 'closed') holidays.add(String(c.day));
  const early = new Map<string, number>();
  for (const e of Array.isArray(params.earlyClosings) ? params.earlyClosings : []) {
    const m = minutesOf(e?.closesAt);
    if (m !== undefined) early.set(String(e.day), m);
  }
  const business = (date: string) => !weekend.has(new Date(Date.parse(`${date}T00:00:00Z`)).getUTCDay()) && !holidays.has(date);
  const nextBusiness = (date: string) => {
    let d = nextDay(date);
    for (let i = 0; i < 366 && !business(d); i++) d = nextDay(d);
    return d;
  };

  const timeZone = String(row.timeZone ?? 'UTC');
  const at = String(header.receivedAt ?? clock.now());
  const l = local(at, timeZone);
  const own = minutesOf(row.cutOff);
  const closes = early.get(l.date);
  const cut = own === undefined ? undefined : closes !== undefined && closes < own ? closes : own;
  const hh = (n: number) => `${String(Math.floor(n / 60)).padStart(2, '0')}:${String(n % 60).padStart(2, '0')}`;
  const base = {
    scheme: row.scheme, direction: row.direction, cutOff: cut === undefined ? row.cutOff : hh(cut), timeZone, afterCutOff: row.afterCutOff,
    receivedAt: at, receivedLocal: l.text, ...(closes !== undefined ? { halfDay: true } : {}),
  };
  // A cut-off time that is not HH:mm takes no file late: the row is the bank's to mend, and the file is not held up by it.
  if (cut === undefined) {
    facts({ outcome: 'taken', result: 'taken', applied: false, reason: 'the cut-off time is not HH:mm', ...base, businessDate: header.businessDate ?? l.date, invalid: true });
    return 'taken';
  }
  if (!business(l.date)) {
    const d = nextBusiness(l.date);
    ctx.header = { ...header, businessDate: d };
    facts({ outcome: 'taken', result: 'taken', applied: false, reason: 'accepted on a day that is no business day', ...base, businessDate: d, movedFrom: l.date });
    return 'taken';
  }
  // In time: the business date stays the one the calendar gave (the day itself, without one).
  if (l.seconds <= cut * 60) {
    facts({ outcome: 'taken', result: 'taken', applied: false, ...base, businessDate: header.businessDate ?? l.date });
    return 'taken';
  }
  if (row.afterCutOff === 'refuse') {
    facts({ outcome: 'refused', result: 'refused', applied: true, reasonCode: 'TM01', ...base });
    return { outcome: 'refused', reasonCode: 'TM01' };
  }
  const d = nextBusiness(l.date);
  ctx.header = { ...header, businessDate: d };
  facts({ outcome: 'taken', result: 'taken', applied: true, ...base, businessDate: d, movedFrom: l.date });
  return 'taken';
}
