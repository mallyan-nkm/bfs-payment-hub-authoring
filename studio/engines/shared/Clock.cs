namespace TransactHub.Engines;

/// <summary>
/// The virtual clock of the harness (R-C-06: the target's own time control). Time moves only when
/// nothing is in flight: no orchestration message waits, no activity runs. Then the clock jumps to
/// the next due item (a durable timer, an activity's latency, a party delivery, a signal) and fires
/// it. A wait of two days therefore takes no wall time, and a run gives the same result every time.
/// This is the same model as the studio's VirtualClock and Temporal's time-skipping test server.
/// </summary>
public sealed class VirtualClock
{
    readonly object gate = new();
    readonly SortedDictionary<(long at, long seq), Action> timers = new();
    long now;
    long seq;
    int busy;
    long progress; // every change of the busy count; a stall is no progress for the stall period
    public readonly long Start;

    public VirtualClock(long startMs) { Start = startMs; now = startMs; }

    public long Now { get { lock (gate) return now; } }
    public DateTime NowUtc => Duration.Utc(Now);
    public long Elapsed => Now - Start;

    public void Schedule(long at, Action fire)
    {
        lock (gate) timers[(Math.Max(at, now), seq++)] = fire;
    }

    public void Enter(int n = 1) { if (n == 0) return; lock (gate) { busy += n; progress++; } }

    public void Exit(int n = 1)
    {
        if (n == 0) return;
        lock (gate)
        {
            busy -= n;
            progress++;
            if (busy < 0) throw new InvalidOperationException("clock: busy count below zero");
            if (busy == 0) Monitor.PulseAll(gate);
        }
    }

    /// <summary>Sleep until a virtual time. The caller is counted as busy; it is not while it sleeps.</summary>
    public Task Sleep(long at)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(at, () => { Enter(); tcs.SetResult(); });
        Exit();
        return tcs.Task;
    }

    /// <summary>A sleep that never ends: an attempt that never answers and has no deadline.</summary>
    public Task SleepForever()
    {
        Exit();
        return new TaskCompletionSource().Task;
    }

    public int PendingTimers { get { lock (gate) return timers.Count; } }

    /// <summary>
    /// Drive the run: wait until idle, stop when done, else fire the next timer. Returns false when
    /// nothing is left to fire or the limit is reached.
    /// </summary>
    public bool RunUntil(Func<bool> done, long limitAt, TimeSpan stall)
    {
        while (true)
        {
            lock (gate)
            {
                var seen = progress;
                var since = DateTime.UtcNow;
                while (busy > 0)
                {
                    Monitor.Wait(gate, TimeSpan.FromSeconds(1));
                    if (progress != seen) { seen = progress; since = DateTime.UtcNow; }
                    else if (DateTime.UtcNow - since > stall) throw new TimeoutException($"stalled with {busy} item(s) in flight at virtual {Duration.Iso(now)}");
                }
            }
            if (done()) return true;
            Action fire;
            lock (gate)
            {
                if (timers.Count == 0) return false;
                var first = timers.First();
                if (first.Key.at > limitAt) return false;
                timers.Remove(first.Key);
                now = first.Key.at;
                busy++;
                fire = first.Value;
            }
            try { fire(); }
            finally { Exit(); }
        }
    }
}
