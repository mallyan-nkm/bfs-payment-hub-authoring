using System.Collections.Concurrent;
using TransactHub.Store;

namespace TransactHub.Engines.Durable;

/// <summary>
/// Writes the facts of many subjects side by side and the facts of one subject in order (I-78). Each subject (a payload, a
/// bulk) has a lane of its own: a fact waits only for the facts of its own subject, never for another payment's. What queues
/// on a lane while a save is in flight goes in the next save, up to <c>maxBatch</c> facts at a time, chained as one would
/// write them one by one (<see cref="FactLog.AppendMany"/>). A caller's task ends when its fact is in the store, so a fact is
/// still written before it is acknowledged; the action given with it runs after the save, in the lane's order.
/// </summary>
public sealed class FactWriter(StoreOptions options, TimeProvider? clock = null, int maxBatch = 256)
{
    sealed record Pending(NewFact Fact, Action? After, TaskCompletionSource Done);

    sealed class Lane(string kind)
    {
        public readonly string Kind = kind;
        public readonly List<Pending> Queue = new();
        public bool Writing;
    }

    readonly ConcurrentDictionary<string, Lane> lanes = new();
    long saves, written;

    /// <summary>Saves made and facts written so far: facts per save is the batching the lanes achieved.</summary>
    public (long saves, long facts) Counts => (Interlocked.Read(ref saves), Interlocked.Read(ref written));

    /// <summary>One fact of a subject; <paramref name="after"/> runs once it is saved, before the task ends, in the subject's order.</summary>
    public Task Append(string subjectId, string subjectKind, NewFact fact, Action? after = null)
    {
        var lane = lanes.GetOrAdd(subjectId, _ => new Lane(subjectKind));
        var p = new Pending(fact, after, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        bool lead;
        lock (lane)
        {
            lane.Queue.Add(p);
            lead = !lane.Writing;
            if (lead) lane.Writing = true;
        }
        if (lead) _ = Task.Run(() => Drain(subjectId, lane));
        return p.Done.Task;
    }

    async Task Drain(string subjectId, Lane lane)
    {
        for (;;)
        {
            List<Pending> batch;
            lock (lane)
            {
                if (lane.Queue.Count == 0) { lane.Writing = false; return; }
                batch = lane.Queue.GetRange(0, Math.Min(maxBatch, lane.Queue.Count));
                lane.Queue.RemoveRange(0, batch.Count);
            }
            try
            {
                await using (var db = StoreDbContext.Open(options))
                    await new FactLog(db, clock).AppendMany(subjectId, lane.Kind, batch.Select(b => b.Fact).ToList());
                Interlocked.Increment(ref saves);
                Interlocked.Add(ref written, batch.Count);
                foreach (var b in batch)
                {
                    try { b.After?.Invoke(); b.Done.SetResult(); }
                    catch (Exception e) { b.Done.SetException(e); }
                }
            }
            catch (Exception e)
            {
                foreach (var b in batch) b.Done.TrySetException(e);
            }
        }
    }
}
