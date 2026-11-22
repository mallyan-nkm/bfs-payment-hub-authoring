using System.Xml;

namespace TransactHub.Engine.Flowable;

/// <summary>Who keeps time and drives jobs: the binding on its virtual clock, or the server in real time.</summary>
public enum FlowableMode
{
    /// <summary>
    /// The server's async executor is off. The binding runs every executable job and moves a timer when its
    /// virtual clock reaches it, so a wait of days takes no wall time (the kit, the tests, R-C-06; Issue I-53).
    /// </summary>
    Virtual,
    /// <summary>
    /// The server's async executor is on and time is real. The binding is a pure external worker: it acquires,
    /// locks, completes and fails jobs and throws BPMN errors. Flowable runs its own jobs, fires its timers,
    /// retries a failed job after its retry timeout and hands a job out again when its lock expires. The
    /// binding places a timer on the business calendar or from a parameter by rescheduling it to the due date
    /// it computes (Issue I-55), and surfaces every dead letter.
    /// </summary>
    Production,
}

/// <summary>
/// How the binding runs. From the environment: TH_FLOWABLE_MODE (virtual, the default, or production),
/// TH_FLOWABLE_LOCK (how long a job is locked to the binding, ISO 8601, default PT10M),
/// TH_FLOWABLE_RETRY_WAIT (how long Flowable waits before it hands a failed job out again, default PT30S),
/// TH_FLOWABLE_POLL (how often the binding asks for work when idle, default PT0.25S),
/// TH_FLOWABLE_MAX_WAIT (how long one run may wait for its payments, default PT1H),
/// TH_FLOWABLE_SIM_SCALE (a simulator reply's delay is multiplied by it, as the stub's --time-scale; default 1),
/// and, for tests only, TH_FLOWABLE_FAULT: crash:&lt;step&gt; (the binding takes the step's first job and stops
/// as a crashed worker would, holding the lock), down:&lt;step&gt; (the step's worker never answers) or exit:&lt;step&gt; (the
/// binding's process exits once the step's answer is recorded in its journal; a binding started with the same TH_FLOWABLE_RUN
/// goes on, Issue I-89).
/// </summary>
public sealed record FlowableOptions
{
    public FlowableMode Mode { get; init; } = FlowableMode.Virtual;
    public TimeSpan LockFor { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan RetryWait { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromHours(1);
    public double SimScale { get; init; } = 1;
    /// <summary>Test only: the step at which the binding acts as a worker that crashed holding its job's lock, once.</summary>
    public string? CrashAt { get; init; }
    /// <summary>Test only: the step whose worker never answers, so the job is retried by Flowable until it is a dead letter.</summary>
    public string? DownAt { get; init; }
    /// <summary>Test only: the step after whose answer the binding's process exits, as a killed host would, once per run (Issue I-89).</summary>
    public string? ExitAt { get; init; }

    public bool Production => Mode == FlowableMode.Production;

    public static FlowableOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    public static FlowableOptions From(Func<string, string?> env)
    {
        static TimeSpan? Dur(string? s) => string.IsNullOrWhiteSpace(s) ? null : XmlConvert.ToTimeSpan(s.Trim());
        var mode = (env("TH_FLOWABLE_MODE") ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "virtual" => FlowableMode.Virtual,
            "production" => FlowableMode.Production,
            var m => throw new ArgumentException($"TH_FLOWABLE_MODE is virtual or production, not {m}"),
        };
        var o = new FlowableOptions { Mode = mode };
        o = o with
        {
            LockFor = Dur(env("TH_FLOWABLE_LOCK")) ?? o.LockFor,
            RetryWait = Dur(env("TH_FLOWABLE_RETRY_WAIT")) ?? o.RetryWait,
            Poll = Dur(env("TH_FLOWABLE_POLL")) ?? o.Poll,
            MaxWait = Dur(env("TH_FLOWABLE_MAX_WAIT")) ?? o.MaxWait,
            SimScale = double.TryParse(env("TH_FLOWABLE_SIM_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s >= 0 ? s : o.SimScale,
        };
        var fault = env("TH_FLOWABLE_FAULT");
        if (!string.IsNullOrWhiteSpace(fault))
        {
            var (kind, step) = fault.Split(':', 2) is [var k, var st] ? (k.Trim(), st.Trim()) : throw new ArgumentException($"TH_FLOWABLE_FAULT is crash:<step>, down:<step> or exit:<step>, not {fault}");
            o = kind switch
            {
                "crash" => o with { CrashAt = step },
                "down" => o with { DownAt = step },
                "exit" => o with { ExitAt = step },
                _ => throw new ArgumentException($"TH_FLOWABLE_FAULT is crash:<step>, down:<step> or exit:<step>, not {fault}"),
            };
        }
        return o;
    }
}
