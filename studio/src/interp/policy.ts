import { durationMs } from '../core/duration.js';

// Effective policy (R-05-16, R-03-15): type default, then activity, then step.
// Each resolved value records where it came from.

export interface PolicyValue<T> { value: T; source: 'type default' | 'activity' | 'step' | 'none' }

export interface EffectivePolicy {
  attemptTimeout: PolicyValue<string | null>;
  totalTimeout: PolicyValue<string | null>;
  maxAttempts: PolicyValue<number>;
  firstInterval: PolicyValue<string>;
  backoff: PolicyValue<number>;
  nonRetryable: PolicyValue<string[] | 'all'>;
}

interface TypeDefault { attemptTimeout: string | null; maxAttempts: number; nonRetryable: string[] | 'all' }

// Part 3, table 3.5. Values are placeholders the engine binding may set per environment.
export const TYPE_DEFAULTS: Record<string, TypeDefault> = {
  map: { attemptTimeout: 'PT5S', maxAttempts: 1, nonRetryable: 'all' },
  validate: { attemptTimeout: 'PT5S', maxAttempts: 1, nonRetryable: 'all' },
  decide: { attemptTimeout: 'PT5S', maxAttempts: 1, nonRetryable: 'all' },
  enrich: { attemptTimeout: 'PT5S', maxAttempts: 3, nonRetryable: [] },
  reconcile: { attemptTimeout: 'PT5S', maxAttempts: 3, nonRetryable: [] },
  protect: { attemptTimeout: 'PT10S', maxAttempts: 3, nonRetryable: [] },
  screen: { attemptTimeout: 'PT10S', maxAttempts: 3, nonRetryable: ['BusinessReject'] },
  exchange: { attemptTimeout: 'PT10S', maxAttempts: 3, nonRetryable: ['BusinessReject'] },
  post: { attemptTimeout: 'PT10S', maxAttempts: 3, nonRetryable: ['BusinessReject'] },
  reserve: { attemptTimeout: 'PT10S', maxAttempts: 3, nonRetryable: ['BusinessReject'] },
  notify: { attemptTimeout: 'PT5S', maxAttempts: 5, nonRetryable: [] },
  hold: { attemptTimeout: null, maxAttempts: 1, nonRetryable: 'all' },
};

export function effectivePolicy(activity: any, stepPolicy?: any): EffectivePolicy {
  const td = TYPE_DEFAULTS[activity.type] ?? { attemptTimeout: 'PT5S', maxAttempts: 1, nonRetryable: 'all' };
  const a = activity.policy ?? {};
  const s = stepPolicy ?? {};
  const pick = <T>(sv: T | undefined, av: T | undefined, tv: T, hasType = true): PolicyValue<T> =>
    sv !== undefined ? { value: sv, source: 'step' } : av !== undefined ? { value: av, source: 'activity' } : { value: tv, source: hasType ? 'type default' : 'none' };
  return {
    attemptTimeout: pick(s.attemptTimeout, a.attemptTimeout, td.attemptTimeout),
    totalTimeout: pick(s.totalTimeout, a.totalTimeout, null, false),
    maxAttempts: pick(s.retry?.maxAttempts, a.retry?.maxAttempts, td.maxAttempts),
    firstInterval: pick(s.retry?.firstInterval, a.retry?.firstInterval, 'PT1S'),
    backoff: pick(s.retry?.backoff, a.retry?.backoff, 2),
    nonRetryable: pick(s.retry?.nonRetryable, a.retry?.nonRetryable, td.nonRetryable),
  };
}

export function policyValues(p: EffectivePolicy) {
  return Object.fromEntries(Object.entries(p).map(([k, v]) => [k, v.value]));
}

export function retryInterval(p: EffectivePolicy, attempt: number): number {
  return durationMs(p.firstInterval.value) * Math.pow(p.backoff.value, attempt - 1);
}

export function isRetryable(p: EffectivePolicy, errorName: string, declared: any[] | undefined): boolean {
  if (p.nonRetryable.value === 'all') return false;
  if ((p.nonRetryable.value as string[]).includes(errorName)) return false;
  const d = (declared ?? []).find((e) => e.name === errorName);
  if (d) return !!d.retryable;
  return errorName === 'Timeout' || errorName === 'TransientError';
}
