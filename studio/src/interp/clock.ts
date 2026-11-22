// The virtual clock (C09-S2) and cancellation scopes.
// One clock drives timers, awaits, backoff and simulator delays. Time moves only when every
// coroutine is blocked: the run loop drains the microtask queue, then jumps to the next timer.
// A wait of days therefore finishes at once, and a run is deterministic.

export class Cancelled extends Error {
  constructor(public reason: any) {
    super(typeof reason === 'string' ? `cancelled: ${reason}` : 'cancelled');
  }
}

export class Scope {
  cancelled: any = undefined;
  private listeners = new Set<(r: any) => void>();
  private children = new Set<Scope>();

  constructor(public parent?: Scope) {
    if (parent) {
      parent.children.add(this);
      if (parent.cancelled !== undefined) this.cancelled = parent.cancelled;
    }
  }

  child(): Scope {
    return new Scope(this);
  }

  get isCancelled() {
    return this.cancelled !== undefined;
  }

  cancel(reason: any) {
    if (this.cancelled !== undefined) return;
    this.cancelled = reason;
    for (const c of [...this.children]) c.cancel(reason);
    for (const l of [...this.listeners]) l(reason);
    this.listeners.clear();
  }

  /** Detach from the parent once the work it guarded is over. */
  dispose() {
    this.parent?.children.delete(this);
    this.listeners.clear();
  }

  onCancel(fn: (r: any) => void): () => void {
    if (this.cancelled !== undefined) {
      fn(this.cancelled);
      return () => {};
    }
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  throwIfCancelled() {
    if (this.cancelled !== undefined) throw new Cancelled(this.cancelled);
  }

  /** A promise that rejects when the scope is cancelled and never resolves otherwise. */
  never(): Promise<never> {
    return new Promise((_, reject) => this.onCancel((r) => reject(new Cancelled(r))));
  }
}

interface Timer {
  at: number;
  seq: number;
  fire: () => void;
  dead: boolean;
  label?: string;
}

const tick = () => new Promise<void>((r) => setImmediate(r));

export class VirtualClock {
  now: number;
  readonly start: number;
  private seq = 0;
  private timers: Timer[] = [];

  constructor(startMs: number) {
    this.now = startMs;
    this.start = startMs;
  }

  get elapsed() {
    return this.now - this.start;
  }

  iso(t = this.now) {
    return new Date(t).toISOString();
  }

  today(t = this.now) {
    return this.iso(t).slice(0, 10);
  }

  /** Run fn at an absolute virtual time. Returns a cancel function. */
  at(t: number, fn: () => void, label?: string): () => void {
    const timer: Timer = { at: Math.max(t, this.now), seq: this.seq++, fire: fn, dead: false, label };
    this.timers.push(timer);
    return () => {
      timer.dead = true;
    };
  }

  /** Sleep on the virtual clock until `t`; rejects with Cancelled if the scope is cancelled. */
  sleepUntil(t: number, scope?: Scope, label?: string): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      if (scope?.isCancelled) return reject(new Cancelled(scope.cancelled));
      let off = () => {};
      const kill = this.at(t, () => {
        off();
        resolve();
      }, label);
      if (scope) off = scope.onCancel((r) => {
        kill();
        reject(new Cancelled(r));
      });
    });
  }

  sleep(ms: number, scope?: Scope, label?: string): Promise<void> {
    return this.sleepUntil(this.now + ms, scope, label);
  }

  pending(): number {
    return this.timers.filter((t) => !t.dead).length;
  }

  /** Work outside this process (a call to the native host): while any is outstanding, the run is not idle. */
  private outside = 0;
  /** Whether work outside this process is under way. */
  get busyOutside() {
    return this.outside > 0;
  }
  async external<T>(work: () => Promise<T>): Promise<T> {
    this.outside++;
    try {
      return await work();
    } finally {
      this.outside--;
    }
  }

  /**
   * Drive the clock until `done()` holds, no timer is left, or the limit is reached.
   * Returns 'done', 'idle' (nothing left to run) or 'limit'.
   */
  async run(done: () => boolean, limitMs = 400 * 86400000): Promise<'done' | 'idle' | 'limit'> {
    for (let guard = 0; guard < 1_000_000; guard++) {
      await tick();
      await tick();
      if (done()) return 'done';
      // Business time does not move while work outside the process is under way: wait for it.
      if (this.outside > 0) {
        await new Promise((r) => setTimeout(r, 1));
        continue;
      }
      this.timers = this.timers.filter((t) => !t.dead);
      if (this.timers.length === 0) return 'idle';
      this.timers.sort((a, b) => a.at - b.at || a.seq - b.seq);
      const next = this.timers.shift()!;
      if (next.at - this.start > limitMs) return 'limit';
      this.now = next.at;
      next.fire();
    }
    return 'limit';
  }
}
