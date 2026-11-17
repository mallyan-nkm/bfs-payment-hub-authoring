// ISO 8601 durations and a business calendar for the virtual clock.

const RE = /^P(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$/;

export interface Duration {
  years: number; months: number; weeks: number; days: number;
  hours: number; minutes: number; seconds: number;
}

export function parseDuration(s: string): Duration {
  const m = RE.exec(s);
  if (!m || s === 'P' || s.endsWith('T')) throw new Error(`Not an ISO 8601 duration: ${s}`);
  const n = (i: number) => (m[i] ? Number(m[i]) : 0);
  return { years: n(1), months: n(2), weeks: n(3), days: n(4), hours: n(5), minutes: n(6), seconds: n(7) };
}

export function isDuration(s: unknown): s is string {
  return typeof s === 'string' && RE.test(s) && s !== 'P';
}

/** Milliseconds for a duration with no calendar parts beyond days (a day is 24 hours). */
export function durationMs(s: string): number {
  const d = parseDuration(s);
  if (d.years || d.months) throw new Error(`Duration ${s} has calendar parts; use addDuration`);
  return ((((d.weeks * 7 + d.days) * 24 + d.hours) * 60 + d.minutes) * 60 + d.seconds) * 1000;
}

export function formatDuration(ms: number): string {
  if (ms === 0) return 'PT0S';
  let s = Math.round(ms / 1000);
  const days = Math.floor(s / 86400); s -= days * 86400;
  const h = Math.floor(s / 3600); s -= h * 3600;
  const m = Math.floor(s / 60); s -= m * 60;
  let out = 'P' + (days ? `${days}D` : '');
  if (h || m || s) out += 'T' + (h ? `${h}H` : '') + (m ? `${m}M` : '') + (s ? `${s}S` : '');
  return out;
}

/** A business calendar: weekend days and holidays. The UAE weekend is Saturday and Sunday. */
export interface BusinessCalendar {
  weekend: number[]; // 0 = Sunday ... 6 = Saturday, in UTC
  holidays: string[]; // ISO dates
}

export const DEFAULT_CALENDAR: BusinessCalendar = { weekend: [0, 6], holidays: [] };

function isBusinessDay(t: number, cal: BusinessCalendar): boolean {
  const d = new Date(t);
  if (cal.weekend.includes(d.getUTCDay())) return false;
  return !cal.holidays.includes(d.toISOString().slice(0, 10));
}

/**
 * Add a duration to an instant. With the business calendar, each whole day counts one
 * business day and lands on a business day; the time part is added as elapsed time.
 */
export function addDuration(t: number, s: string, calendar?: BusinessCalendar): number {
  const d = parseDuration(s);
  let out = t;
  if (d.years || d.months) {
    const date = new Date(out);
    date.setUTCFullYear(date.getUTCFullYear() + d.years, date.getUTCMonth() + d.months);
    out = date.getTime();
  }
  const days = d.weeks * 7 + d.days;
  if (calendar) {
    for (let i = 0; i < days; i++) {
      out += 86400000;
      while (!isBusinessDay(out, calendar)) out += 86400000;
    }
  } else {
    out += days * 86400000;
  }
  out += ((d.hours * 60 + d.minutes) * 60 + d.seconds) * 1000;
  return out;
}
