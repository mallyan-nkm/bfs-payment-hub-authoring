// Exact decimal arithmetic for money (R-02-17, R-04-23; Issue I-96).
//
// A small fixed-point module on BigInt rather than a library: the rule language needs only
// add, subtract, multiply, a defined division, comparison and one rounding, and a value is
// `units * 10^-scale` with no precision limit, so nothing can overflow or round by surprise.
// It has no dependency to pin or audit, and it states the same semantics as .NET `decimal`
// and Java `BigDecimal` for these operations.
//
// Reading: a JSON number is read by its text, the shortest form that gives the same double
// (`String(n)`), never by its binary value, so 0.1 is one tenth. A decimal string such as
// "90071992547409.93" is read exactly. Writing: a result with at most 15 significant digits
// is given back as a number (every such decimal survives a double exactly); a longer one is
// given back as a Decimal, which serialises as a decimal string.

const RE = /^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/;

/** Digits a JSON number may carry and still be exact in binary floating point (R-02-17). */
export const NUMBER_DIGITS = 15;

/** The places `/` keeps, half away from zero (R-04-23). */
export const DIVISION_PLACES = 20;

const TEN = 10n;
const pow10 = (n: number) => TEN ** BigInt(n);
const abs = (x: bigint) => (x < 0n ? -x : x);

export class Decimal {
  private constructor(readonly units: bigint, readonly scale: number) {}

  static of(units: bigint, scale: number): Decimal {
    while (scale > 0 && units % TEN === 0n) { units /= TEN; scale--; }
    if (scale < 0) { units *= pow10(-scale); scale = 0; }
    return new Decimal(units, scale);
  }

  static readonly ZERO = new Decimal(0n, 0);

  /** The exact decimal a value stands for, or undefined when it is not a number or a decimal string. */
  static parse(v: unknown): Decimal | undefined {
    if (v instanceof Decimal) return v;
    if (typeof v === 'number') return Number.isFinite(v) ? Decimal.text(String(v)) : undefined;
    if (typeof v === 'bigint') return Decimal.of(v, 0);
    if (typeof v === 'boolean') return v ? Decimal.of(1n, 0) : Decimal.ZERO;
    if (typeof v === 'string') return Decimal.text(v.trim());
    return undefined;
  }

  private static text(s: string): Decimal | undefined {
    const m = RE.exec(s);
    if (!m || (!m[2] && !m[3])) return undefined;
    const frac = m[3] ?? '';
    const units = BigInt((m[2] || '0') + frac) * (m[1] === '-' ? -1n : 1n);
    return Decimal.of(units, frac.length - Number(m[4] ?? 0));
  }

  private align(o: Decimal): [bigint, bigint, number] {
    const s = Math.max(this.scale, o.scale);
    return [this.units * pow10(s - this.scale), o.units * pow10(s - o.scale), s];
  }

  plus(o: Decimal) { const [a, b, s] = this.align(o); return Decimal.of(a + b, s); }
  minus(o: Decimal) { const [a, b, s] = this.align(o); return Decimal.of(a - b, s); }
  times(o: Decimal) { return Decimal.of(this.units * o.units, this.scale + o.scale); }
  neg() { return Decimal.of(-this.units, this.scale); }
  isZero() { return this.units === 0n; }
  cmp(o: Decimal): number { const [a, b] = this.align(o); return a < b ? -1 : a > b ? 1 : 0; }

  /** The quotient to `places` decimals, half away from zero; undefined for a zero divisor. */
  div(o: Decimal, places = DIVISION_PLACES): Decimal | undefined {
    if (o.isZero()) return undefined;
    const n = this.units * pow10(o.scale + places);
    const d = o.units * pow10(this.scale);
    let q = n / d;
    const r = n % d;
    if (abs(r) * 2n >= abs(d)) q += (n < 0n) === (d < 0n) ? 1n : -1n;
    return Decimal.of(q, places);
  }

  /** The remainder with the sign of the dividend, as JavaScript's and C#'s `%`; undefined for a zero divisor. */
  mod(o: Decimal): Decimal | undefined {
    if (o.isZero()) return undefined;
    const [a, b, s] = this.align(o);
    return Decimal.of(a % b, s);
  }

  /** Rounded to `places` decimals, half away from zero: 1500.005 is 1500.01 and -0.125 is -0.13. */
  round(places: number): Decimal {
    if (this.scale <= places) return this;
    const d = pow10(this.scale - places);
    let q = this.units / d;
    if (abs(this.units % d) * 2n >= d) q += this.units < 0n ? -1n : 1n;
    return Decimal.of(q, places);
  }

  /** The canonical text: no exponent, no trailing zeros, no "-0". */
  toString(): string {
    const neg = this.units < 0n;
    let digits = abs(this.units).toString();
    if (this.scale === 0) return (neg ? '-' : '') + digits;
    digits = digits.padStart(this.scale + 1, '0');
    return (neg ? '-' : '') + digits.slice(0, -this.scale) + '.' + digits.slice(-this.scale);
  }

  toJSON(): string { return this.toString(); }

  /** Significant digits of the canonical text: the digits once leading zeros are dropped. */
  digits(): number { return abs(this.units).toString().replace(/^0+/, '').length; }

  /** A number when it has at most 15 significant digits, so it is exact either way; else this Decimal. */
  toValue(): number | Decimal { return this.digits() <= NUMBER_DIGITS ? Number(this.toString()) : this; }
}

/** The significant digits of a number or decimal string as a reader takes it (L40); 0 when it is neither. */
export function significantDigits(v: unknown): number {
  return Decimal.parse(v)?.digits() ?? 0;
}

/** A value with every Decimal in it replaced by its decimal string, for JSON and traces. */
export function plain(v: any): any {
  if (v instanceof Decimal) return v.toString();
  if (Array.isArray(v)) return v.map(plain);
  if (v && typeof v === 'object' && Object.getPrototypeOf(v) === Object.prototype) {
    const out: any = {};
    for (const [k, x] of Object.entries(v)) out[k] = plain(x);
    return out;
  }
  return v;
}

const D = (v: unknown) => Decimal.parse(v) ?? Decimal.ZERO;
const out = (d: Decimal | undefined) => (d === undefined ? null : plain(d.toValue()));

/**
 * Money helpers for a script-behind, on `rules.money` (TypeScript) and `api.rules.money` (Python).
 * Each takes numbers or decimal strings and gives a number of at most 15 significant digits or a
 * decimal string, exactly as the rule language computes (R-04-23).
 */
export const moneyHelpers = Object.freeze({
  add: (...xs: unknown[]) => out(xs.reduce<Decimal>((a, x) => a.plus(D(x)), Decimal.ZERO)),
  sub: (a: unknown, b: unknown) => out(D(a).minus(D(b))),
  mul: (a: unknown, b: unknown) => out(D(a).times(D(b))),
  div: (a: unknown, b: unknown) => out(D(a).div(D(b))),
  sum: (xs: unknown[], f?: (x: any) => unknown) => out((Array.isArray(xs) ? xs : []).reduce<Decimal>((a, x) => a.plus(D(f ? f(x) : x)), Decimal.ZERO)),
  round2: (x: unknown) => out(D(x).round(2)),
  cmp: (a: unknown, b: unknown) => D(a).cmp(D(b)),
  eq: (a: unknown, b: unknown) => D(a).cmp(D(b)) === 0,
  str: (x: unknown) => D(x).toString(),
});
