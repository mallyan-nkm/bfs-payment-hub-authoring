import { AsyncLocalStorage } from 'node:async_hooks';

/**
 * The distribution the attempt in progress names (R-09-130, Issue I-222): the engine sends the payment's distribution with each
 * attempt, and the worker hands it to the native host, so one worker set can serve several distributions. Unset when the attempt
 * names none (the studio's own runs, an engine of an older release).
 */
const current = new AsyncLocalStorage<string | undefined>();

export function withAttemptDistribution<T>(distribution: string | undefined, work: () => Promise<T>): Promise<T> {
  return current.run(distribution || undefined, work);
}

export function attemptDistribution(): string | undefined {
  return current.getStore();
}
