using System.Text;
using DurableTask.Core;
using DurableTask.Core.History;
using Newtonsoft.Json;

using TransactHub.Engines;

namespace DtfxHarness;

/// <summary>
/// An in-process orchestration service with virtual time, written for this harness on the model of
/// DurableTask.Emulator's LocalOrchestrationService. The emulator fires timers against
/// DateTime.UtcNow, polling once a second, and its queues poll every 500 ms, so a kit wait of a day
/// would take a day. Here durable timers are held on the virtual clock, queues signal instead of
/// polling, and one work item runs at a time in message order, so a run is deterministic. History is
/// stored serialised, as the emulator stores it, and every episode replays it from the start.
/// </summary>
public sealed class VirtualTimeOrchestrationService : IOrchestrationService, IOrchestrationServiceClient
{
    static readonly JsonSerializerSettings HistorySettings = new() { TypeNameHandling = TypeNameHandling.Objects };

    public sealed class InstanceRecord
    {
        public string Id = "";
        public string Name = "";
        public string? Input;
        public long CreatedSeq;
        public byte[]? History; // serialised history of the current execution
        public List<string> ArchivedHistories = new(); // earlier executions (continue-as-new)
        public readonly List<(long seq, TaskMessage msg)> Pending = new();
        public bool Locked;
        public bool Completed;
        public OrchestrationState? State;
        public string? CustomStatus;
        public string? ParentId;
    }

    readonly object gate = new();
    readonly VirtualClock clock;
    readonly Dictionary<string, InstanceRecord> instances = new();
    readonly List<string> creationOrder = new();
    readonly LinkedList<(long seq, TaskMessage msg)> activityQueue = new();
    long msgSeq;
    object? baton; // the work item that runs now; null when free
    int batonWaiters;
    TaskCompletionSource changed = NewSignal();
    /// <summary>
    /// Where the service writes each change before it makes it, when it serves a distribution on the runtime (Issue I-165): a
    /// message an instance takes, an episode it ran (its new history events, the activities and timers it scheduled, its state),
    /// an activity whose answer was dropped. Null in a kit run.
    /// </summary>
    public IEngineHistory? History;
    public int DroppedMessages;
    public int Episodes; // orchestration work items; each replays the stored history from the start
    public int ActivityRuns;

    public VirtualTimeOrchestrationService(VirtualClock clock) { this.clock = clock; }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    void Signal() { var old = changed; changed = NewSignal(); old.TrySetResult(); }

    async Task<bool> WaitChange(Task signal, TimeSpan timeout, CancellationToken ct)
    {
        var delay = Task.Delay(timeout, ct);
        var w = await Task.WhenAny(signal, delay);
        return w == signal;
    }

    // ------------------------------------------------------------------ instance records

    public IReadOnlyList<InstanceRecord> Instances { get { lock (gate) return creationOrder.Select(i => instances[i]).ToList(); } }
    public InstanceRecord? Find(string id) { lock (gate) return instances.TryGetValue(id, out var r) ? r : null; }

    /// <summary>Every history event of an instance, all executions, as stored.</summary>
    public IEnumerable<string> StoredHistories(InstanceRecord r)
    {
        lock (gate)
        {
            var l = new List<string>(r.ArchivedHistories);
            if (r.History is not null) l.Add(Encoding.UTF8.GetString(r.History));
            return l;
        }
    }

    static string Ser(object o) => JsonConvert.SerializeObject(o, HistorySettings);
    static T De<T>(string s) => JsonConvert.DeserializeObject<T>(s, HistorySettings)!;

    /// <param name="activity">The activity work item whose answer this is, so that a service rebuilt from its history knows it ran.</param>
    /// <param name="replay">Taken from the history while the service is rebuilt: nothing is written and the clock is told later.</param>
    void Enqueue(TaskMessage m, string? activity = null, bool replay = false)
    {
        // Called under the lock. A message for a completed instance is dropped, as a store would.
        var id = m.OrchestrationInstance.InstanceId;
        if (!instances.TryGetValue(id, out var rec))
        {
            if (m.Event is not ExecutionStartedEvent es) { Dropped(activity, replay); return; }
            if (!replay) Record(id, activity, m);
            rec = new InstanceRecord { Id = id, Name = es.Name, Input = es.Input, CreatedSeq = msgSeq, ParentId = es.ParentInstance?.OrchestrationInstance?.InstanceId };
            instances[id] = rec;
            creationOrder.Add(id);
        }
        else if (rec.Completed) { Dropped(activity, replay); return; }
        else if (!replay) Record(id, activity, m);
        rec.Pending.Add((msgSeq++, m));
        if (!replay) clock.Enter();
    }

    void Record(string id, string? activity, TaskMessage m) =>
        History?.Append("message", new System.Text.Json.Nodes.JsonObject { ["instance"] = id, ["activity"] = activity, ["msg"] = Ser(m) });

    void Dropped(string? activity, bool replay)
    {
        DroppedMessages++;
        if (activity is not null && !replay) History?.Append("activity-done", new System.Text.Json.Nodes.JsonObject { ["activity"] = activity });
    }

    // ------------------------------------------------------------------ baton (one work item at a time)

    (long seq, InstanceRecord? inst) BestOrchestration()
    {
        long best = long.MaxValue;
        InstanceRecord? pick = null;
        foreach (var id in creationOrder)
        {
            var r = instances[id];
            if (r.Locked || r.Completed || r.Pending.Count == 0) continue;
            var s = r.Pending.Min(p => p.seq);
            if (s < best) { best = s; pick = r; }
        }
        return (best, pick);
    }

    long BestActivity() => activityQueue.Count > 0 ? activityQueue.First!.Value.seq : long.MaxValue;

    /// <summary>An activity that wakes from a sleep takes the baton back before it carries on.</summary>
    public async Task AcquireBaton(object holder)
    {
        lock (gate) batonWaiters++;
        while (true)
        {
            Task sig;
            lock (gate)
            {
                if (baton is null) { baton = holder; batonWaiters--; return; }
                sig = changed.Task;
            }
            await sig;
        }
    }

    public void ReleaseBaton(object holder)
    {
        lock (gate)
        {
            if (ReferenceEquals(baton, holder)) baton = null;
            Signal();
        }
    }

    // ------------------------------------------------------------------ IOrchestrationService

    public int TaskOrchestrationDispatcherCount => 1;
    public int MaxConcurrentTaskOrchestrationWorkItems => 1;
    public BehaviorOnContinueAsNew EventBehaviourForContinueAsNew => BehaviorOnContinueAsNew.Carryover;
    public int TaskActivityDispatcherCount => 1;
    public int MaxConcurrentTaskActivityWorkItems => 2000; // sleeping activities hold a slot each

    public Task StartAsync() => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task StopAsync(bool isForced) => Task.CompletedTask;
    public Task CreateAsync() => Task.CompletedTask;
    public Task CreateAsync(bool recreateInstanceStore) => Task.CompletedTask;
    public Task CreateIfNotExistsAsync() => Task.CompletedTask;
    public Task DeleteAsync() => Task.CompletedTask;
    public Task DeleteAsync(bool deleteInstanceStore) => Task.CompletedTask;
    public bool IsMaxMessageCountExceeded(int currentMessageCount, OrchestrationRuntimeState runtimeState) => false;
    public int GetDelayInSecondsAfterOnProcessException(Exception exception) => 0;
    public int GetDelayInSecondsAfterOnFetchException(Exception exception) => 0;

    public async Task<TaskOrchestrationWorkItem> LockNextTaskOrchestrationWorkItemAsync(TimeSpan receiveTimeout, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + receiveTimeout;
        while (!cancellationToken.IsCancellationRequested)
        {
            Task sig;
            lock (gate)
            {
                if (baton is null && batonWaiters == 0)
                {
                    var (s, r) = BestOrchestration();
                    if (r is not null && s < BestActivity())
                    {
                        var wi = new TaskOrchestrationWorkItem
                        {
                            InstanceId = r.Id,
                            NewMessages = r.Pending.Select(p => p.msg).ToList(),
                            LockedUntilUtc = DateTime.MaxValue,
                            OrchestrationRuntimeState = r.History is null ? new OrchestrationRuntimeState() : new OrchestrationRuntimeState(JsonConvert.DeserializeObject<IList<HistoryEvent>>(Encoding.UTF8.GetString(r.History), HistorySettings)),
                        };
                        r.Locked = true;
                        Episodes++;
                        baton = wi;
                        lockedCounts[r.Id] = r.Pending.Count;
                        // The history as stored when the episode began: the dispatcher adds the new messages to this runtime state.
                        lockedHistory[r.Id] = (r.History is null ? 0 : wi.OrchestrationRuntimeState.Events.Count, wi.OrchestrationRuntimeState.OrchestrationInstance?.ExecutionId);
                        return wi;
                    }
                }
                sig = changed.Task;
            }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null!;
            await WaitChange(sig, left, cancellationToken);
        }
        return null!;
    }

    readonly Dictionary<string, int> lockedCounts = new();
    readonly Dictionary<string, (int events, string? execution)> lockedHistory = new();

    public Task CompleteTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem, OrchestrationRuntimeState newOrchestrationRuntimeState, IList<TaskMessage> outboundMessages, IList<TaskMessage> orchestratorMessages, IList<TaskMessage> timerMessages, TaskMessage continuedAsNewMessage, OrchestrationState orchestrationState)
    {
        lock (gate)
        {
            var rec = instances[workItem.InstanceId];
            if (History is not null) RecordEpisode(rec, workItem, newOrchestrationRuntimeState, outboundMessages, timerMessages, orchestrationState);
            // New work first, then retire the messages this episode consumed, so the clock never sees a false idle.
            foreach (var m in outboundMessages ?? Array.Empty<TaskMessage>()) { activityQueue.AddLast((msgSeq++, m)); clock.Enter(); }
            foreach (var m in orchestratorMessages ?? Array.Empty<TaskMessage>()) Enqueue(m);
            if (continuedAsNewMessage is not null) Enqueue(continuedAsNewMessage);
            foreach (var m in timerMessages ?? Array.Empty<TaskMessage>())
            {
                var fireAt = Duration.MsOf(((TimerFiredEvent)m.Event).FireAt);
                var msg = m;
                clock.Schedule(fireAt, () => { lock (gate) { Enqueue(msg); Signal(); } });
            }
            var consumed = lockedCounts[rec.Id];
            lockedCounts.Remove(rec.Id);
            var taken = rec.Pending.Take(consumed).ToList();
            rec.Pending.RemoveRange(0, consumed);
            if (newOrchestrationRuntimeState is not null)
            {
                var newExec = newOrchestrationRuntimeState.OrchestrationInstance?.ExecutionId;
                var oldExec = workItem.OrchestrationRuntimeState?.OrchestrationInstance?.ExecutionId;
                if (rec.History is not null && oldExec is not null && newExec != oldExec) rec.ArchivedHistories.Add(Encoding.UTF8.GetString(rec.History));
                rec.History = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(newOrchestrationRuntimeState.Events, HistorySettings));
            }
            if (orchestrationState is not null)
            {
                rec.State = orchestrationState;
                rec.CustomStatus = orchestrationState.Status;
                var st = orchestrationState.OrchestrationStatus;
                if (st is OrchestrationStatus.Completed or OrchestrationStatus.Failed or OrchestrationStatus.Terminated or OrchestrationStatus.Canceled)
                {
                    rec.Completed = true;
                    // Messages that reached a completed instance are dropped.
                    DroppedMessages += rec.Pending.Count;
                    clock.Exit(rec.Pending.Count);
                    rec.Pending.Clear();
                }
            }
            rec.Locked = false;
            if (ReferenceEquals(baton, workItem)) baton = null;
            clock.Exit(taken.Count);
            Signal();
        }
        return Task.CompletedTask;
    }

    public Task AbandonTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem)
    {
        lock (gate)
        {
            var rec = instances[workItem.InstanceId];
            rec.Locked = false;
            lockedCounts.Remove(rec.Id);
            if (ReferenceEquals(baton, workItem)) baton = null;
            Signal();
        }
        return Task.CompletedTask;
    }

    public Task ReleaseTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem) => Task.CompletedTask;
    public Task RenewTaskOrchestrationWorkItemLockAsync(TaskOrchestrationWorkItem workItem) => Task.CompletedTask;

    public async Task<TaskActivityWorkItem> LockNextTaskActivityWorkItem(TimeSpan receiveTimeout, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + receiveTimeout;
        while (!cancellationToken.IsCancellationRequested)
        {
            Task sig;
            lock (gate)
            {
                if (baton is null && batonWaiters == 0 && activityQueue.Count > 0 && BestActivity() < BestOrchestration().seq)
                {
                    var (s, m) = activityQueue.First!.Value;
                    activityQueue.RemoveFirst();
                    var wi = new TaskActivityWorkItem { Id = s.ToString(), TaskMessage = m, LockedUntilUtc = DateTime.MaxValue };
                    baton = wi;
                    ActivityRuns++;
                    return wi;
                }
                sig = changed.Task;
            }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null!;
            await WaitChange(sig, left, cancellationToken);
        }
        return null!;
    }

    public Task CompleteTaskActivityWorkItemAsync(TaskActivityWorkItem workItem, TaskMessage responseMessage)
    {
        lock (gate)
        {
            Enqueue(responseMessage, activity: workItem.Id);
            if (ReferenceEquals(baton, workItem) || baton is ActivityBaton ab && ab.WorkItemId == workItem.Id) baton = null;
            clock.Exit();
            Signal();
        }
        return Task.CompletedTask;
    }

    public Task AbandonTaskActivityWorkItemAsync(TaskActivityWorkItem workItem)
    {
        lock (gate)
        {
            activityQueue.AddFirst((long.Parse(workItem.Id), workItem.TaskMessage));
            if (ReferenceEquals(baton, workItem)) baton = null;
            Signal();
        }
        return Task.CompletedTask;
    }

    public Task<TaskActivityWorkItem> RenewTaskActivityWorkItemLockAsync(TaskActivityWorkItem workItem) => Task.FromResult(workItem);

    /// <summary>The baton an activity holds after it wakes from a sleep.</summary>
    public sealed class ActivityBaton { public string WorkItemId = ""; }

    /// <summary>The work item that holds the baton now. An activity reads it when it starts, as it is then the holder.</summary>
    public object? CurrentBaton { get { lock (gate) return baton; } }

    // ------------------------------------------------------------------ the history (Issue I-165)

    /// <summary>
    /// An episode as a fact, before its effects are made: the history events it added (and from which event), the activities it
    /// scheduled with their numbers, the timers it set, how many messages it consumed and the instance's state. The messages it sends
    /// to other instances and its continue-as-new are recorded as those instances take them.
    /// </summary>
    void RecordEpisode(InstanceRecord rec, TaskOrchestrationWorkItem workItem, OrchestrationRuntimeState? state, IList<TaskMessage>? outbound, IList<TaskMessage>? timers, OrchestrationState? orchestrationState)
    {
        var e = new System.Text.Json.Nodes.JsonObject { ["instance"] = rec.Id, ["consumed"] = lockedCounts[rec.Id] };
        if (state is not null)
        {
            var newExec = state.OrchestrationInstance?.ExecutionId;
            var (stored, oldExec) = lockedHistory.TryGetValue(rec.Id, out var h) ? h : (0, null);
            var from = rec.History is not null && oldExec is not null && newExec == oldExec ? stored : 0;
            e["execution"] = newExec;
            e["from"] = from;
            e["events"] = Ser(state.Events.Skip(from).ToList());
        }
        var seqNo = msgSeq;
        e["activities"] = new System.Text.Json.Nodes.JsonArray((outbound ?? Array.Empty<TaskMessage>()).Select(m => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject { ["seq"] = seqNo++, ["msg"] = Ser(m) }).ToArray());
        e["timers"] = new System.Text.Json.Nodes.JsonArray((timers ?? Array.Empty<TaskMessage>()).Select(m => (System.Text.Json.Nodes.JsonNode?)Ser(m)).ToArray());
        if (orchestrationState is not null) e["state"] = Ser(orchestrationState);
        History!.Append("episode", e);
    }

    static string TimerKey(TaskMessage m) => $"{m.OrchestrationInstance.InstanceId}|{((TimerFiredEvent)m.Event).TimerId}";

    static bool Ended(OrchestrationStatus s) => s is OrchestrationStatus.Completed or OrchestrationStatus.Failed or OrchestrationStatus.Terminated or OrchestrationStatus.Canceled;

    /// <summary>
    /// The service as its history leaves it (Issue I-165): every instance with its history, the messages it has not consumed, the
    /// activities scheduled and not answered (an activity that ran when the service stopped runs again: at least once, as a job
    /// whose lock expired), and the timers set and not fired, which fire at their time on the clock (at once when it has passed).
    /// Called before the worker starts. Answers what it rebuilt.
    /// </summary>
    public System.Text.Json.Nodes.JsonObject Rebuild(IEnumerable<(string code, System.Text.Json.Nodes.JsonObject data)> facts)
    {
        var timers = new Dictionary<string, TaskMessage>(StringComparer.Ordinal);
        var activities = new SortedDictionary<long, TaskMessage>();
        int episodes = 0, messages = 0;
        lock (gate)
        {
            foreach (var (code, d) in facts)
            {
                switch (code)
                {
                    case "message":
                    {
                        var m = De<TaskMessage>((string)d["msg"]!);
                        messages++;
                        if ((string?)d["activity"] is string act && long.TryParse(act, out var a)) activities.Remove(a);
                        if (m.Event is TimerFiredEvent) timers.Remove(TimerKey(m));
                        Enqueue(m, replay: true);
                        break;
                    }
                    case "activity-done":
                        if (long.TryParse((string?)d["activity"], out var done)) activities.Remove(done);
                        break;
                    case "episode":
                    {
                        episodes++;
                        var rec = instances[(string)d["instance"]!];
                        var consumed = (int)d["consumed"]!;
                        if ((string?)d["events"] is string evs)
                        {
                            var added = De<List<HistoryEvent>>(evs);
                            var from = (int)d["from"]!;
                            var all = from == 0 || rec.History is null ? new List<HistoryEvent>() : De<List<HistoryEvent>>(Encoding.UTF8.GetString(rec.History)).Take(from).ToList();
                            if (from == 0 && rec.History is not null) rec.ArchivedHistories.Add(Encoding.UTF8.GetString(rec.History));
                            all.AddRange(added);
                            rec.History = Encoding.UTF8.GetBytes(Ser(all));
                        }
                        foreach (var a in d["activities"]!.AsArray())
                        {
                            var seqNo = (long)a!["seq"]!;
                            activities[seqNo] = De<TaskMessage>((string)a["msg"]!);
                            msgSeq = Math.Max(msgSeq, seqNo + 1);
                        }
                        foreach (var t in d["timers"]!.AsArray())
                        {
                            var m = De<TaskMessage>((string)t!);
                            timers[TimerKey(m)] = m;
                        }
                        rec.Pending.RemoveRange(0, Math.Min(consumed, rec.Pending.Count));
                        if ((string?)d["state"] is string st)
                        {
                            rec.State = De<OrchestrationState>(st);
                            rec.CustomStatus = rec.State.Status;
                            if (Ended(rec.State.OrchestrationStatus)) { rec.Completed = true; rec.Pending.Clear(); }
                        }
                        Episodes++;
                        break;
                    }
                }
            }
            // What is still to do counts as in flight on the clock, as it did when it was first queued.
            var pending = instances.Values.Sum(r => r.Completed ? 0 : r.Pending.Count);
            foreach (var (seqNo, m) in activities)
            {
                if (instances.TryGetValue(m.OrchestrationInstance.InstanceId, out var r) && r.Completed) continue;
                activityQueue.AddLast((seqNo, m));
            }
            clock.Enter(pending + activityQueue.Count);
            var due = 0;
            foreach (var (_, m) in timers)
            {
                if (instances.TryGetValue(m.OrchestrationInstance.InstanceId, out var r) && r.Completed) continue;
                var msg = m;
                due++;
                clock.Schedule(Duration.MsOf(((TimerFiredEvent)m.Event).FireAt), () => { lock (gate) { Enqueue(msg); Signal(); } });
            }
            Signal();
            return new System.Text.Json.Nodes.JsonObject
            {
                ["instances"] = instances.Count, ["open"] = instances.Values.Count(r => !r.Completed), ["episodes"] = episodes, ["messages"] = messages,
                ["pendingMessages"] = pending, ["activitiesAgain"] = activityQueue.Count, ["timers"] = due,
            };
        }
    }

    // ------------------------------------------------------------------ IOrchestrationServiceClient

    public Task CreateTaskOrchestrationAsync(TaskMessage creationMessage) => CreateTaskOrchestrationAsync(creationMessage, null!);

    public Task CreateTaskOrchestrationAsync(TaskMessage creationMessage, OrchestrationStatus[] dedupeStatuses)
    {
        lock (gate) { Enqueue(creationMessage); Signal(); }
        return Task.CompletedTask;
    }

    public Task SendTaskOrchestrationMessageAsync(TaskMessage message) => SendTaskOrchestrationMessageBatchAsync(message);

    public Task SendTaskOrchestrationMessageBatchAsync(params TaskMessage[] messages)
    {
        lock (gate) { foreach (var m in messages) Enqueue(m); Signal(); }
        return Task.CompletedTask;
    }

    public async Task<OrchestrationState> WaitForOrchestrationAsync(string instanceId, string executionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            Task sig;
            lock (gate)
            {
                if (instances.TryGetValue(instanceId, out var r) && r.Completed) return r.State!;
                sig = changed.Task;
            }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException();
            await WaitChange(sig, left, cancellationToken);
        }
    }

    public Task ForceTerminateTaskOrchestrationAsync(string instanceId, string reason) => throw new NotSupportedException();

    public Task<IList<OrchestrationState>> GetOrchestrationStateAsync(string instanceId, bool allExecutions)
    {
        lock (gate)
        {
            IList<OrchestrationState> l = instances.TryGetValue(instanceId, out var r) && r.State is not null ? new List<OrchestrationState> { r.State } : new List<OrchestrationState>();
            return Task.FromResult(l);
        }
    }

    public Task<OrchestrationState> GetOrchestrationStateAsync(string instanceId, string executionId)
    {
        lock (gate) return Task.FromResult(instances.TryGetValue(instanceId, out var r) ? r.State! : null!);
    }

    public Task<string> GetOrchestrationHistoryAsync(string instanceId, string executionId)
    {
        lock (gate) return Task.FromResult(instances.TryGetValue(instanceId, out var r) && r.History is not null ? Encoding.UTF8.GetString(r.History) : "");
    }

    public Task PurgeOrchestrationHistoryAsync(DateTime thresholdDateTimeUtc, OrchestrationStateTimeRangeFilterType timeRangeFilterType) => Task.CompletedTask;
}
