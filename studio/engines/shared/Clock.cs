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
    /// <summary>
    /// While this says so, the clock fires only what is due now and does not jump ahead: a live operator may still answer an
    /// operator wait (LiveSignals.HoldClock). The feed's deliveries are scheduled at now, so they still fire.
    /// </summary>
    public Func<bool>? Hold;

    /// <summary>
    /// Real time (the long-running engine host, R-09-93): the clock follows the wall clock from its start, and a timer fires when the
    /// wall clock reaches it, never sooner, so a wait for a scheme's message is as long as its timeout says and a message the host
    /// delivers meanwhile (LiveSignals, kind deliver) reaches the run. Set by TH_HARNESS_REAL_TIME=1.
    /// </summary>
    public bool RealTime = Environment.GetEnvironmentVariable("TH_HARNESS_REAL_TIME") == "1";
    readonly DateTime wallStart;

    /// <summary>The wall clock as this clock's time.</summary>
    long WallNow => Start + (long)(DateTime.UtcNow - wallStart).TotalMilliseconds;

    public VirtualClock(long startMs)
    {
        Start = startMs;
        now = startMs;
        // In real time, a run whose start is the wall clock's moment (the engine host's, R-09-135) follows the wall clock itself, not
        // the wall clock less the time the harness took to start: a value read as of a step reads it as of the step's real time.
        var wall = DateTime.UtcNow;
        wallStart = RealTime && Math.Abs((wall - Duration.Utc(startMs)).TotalMinutes) < 10 ? Duration.Utc(startMs) : wall;
    }

    public long Now { get { lock (gate) return now; } }
    public DateTime NowUtc => Duration.Utc(Now);
    public long Elapsed => Now - Start;

    public void Schedule(long at, Action fire)
    {
        // A wait for the wall clock (real time) wakes to take it: an answer handed back from another thread fires now, not later.
        lock (gate) { timers[(Math.Max(at, now), seq++)] = fire; Monitor.PulseAll(gate); }
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
                if (RealTime)
                {
                    // Time passes as the wall clock does: what is due fires, the rest waits; with nothing scheduled the run waits for
                    // a delivery until its limit, instead of ending.
                    var wall = WallNow;
                    if (wall > now) now = wall;
                    if (now > limitAt) return false;
                    if (timers.Count == 0 || timers.First().Key.at > now)
                    {
                        var wait = timers.Count == 0 ? 200 : Math.Clamp(timers.First().Key.at - now, 1, 200);
                        progress++;
                        Monitor.Wait(gate, TimeSpan.FromMilliseconds(wait));
                        continue;
                    }
                }
                if (timers.Count == 0 && Hold?.Invoke() != true) return false;
                if ((timers.Count == 0 || timers.First().Key.at > now) && Hold?.Invoke() == true)
                {
                    progress++;
                    Monitor.Wait(gate, TimeSpan.FromMilliseconds(200));
                    continue;
                }
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
