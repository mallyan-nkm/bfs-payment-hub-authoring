using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Model;
using TransactHub.Store;
using Xunit;

namespace TransactHub.Store.Tests;

// A trace id on every fact (R-09-69, Issue I-80) and an engine's failure as a fact (R-09-70, Issue I-103), on SQL Server and
// PostgreSQL, through the store's migrations (expand-then-contract: the columns are added nullable).

[Collection(DatabaseCollection.Name)]
public sealed class TraceIdTests(Databases dbs)
{
    static readonly DateTimeOffset T0 = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);
    static readonly ActivitySource Source = new("TransactHub.Store.Tests.TraceIds");

    static CreditTransfer Payment(string e2e) => new()
    {
        PaymentId = new PaymentIdentification { EndToEndId = e2e, Uetr = Guid.NewGuid().ToString() },
        InstructedAmount = Money.Aed(100m),
        Creditor = new Party { Name = "Synthetic Payee", OtherIds = [new GenericId("10000000000009", "PERSON-CODE")] },
        CreditorAccount = new Account { Iban = "AE070000000000000000099" },
        CreditorAgent = new Agent { ClearingSystem = "AEUAE", MemberId = "000000002" },
    };

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task A_fact_names_the_trace_and_span_it_was_written_in_outside_its_hash(StoreProvider p)
    {
        using var listener = new ActivityListener { ShouldListenTo = s => s.Name == Source.Name, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded };
        ActivitySource.AddActivityListener(listener);
        await using var db = await dbs.Open(p);
        var id = await new PaymentStore(db, new TestClock(T0)).AcceptPayment("example-bank", "uae.p", Direction.Outward, Payment("E2E-T1"), "uae.p@1.0.0");
        var log = new FactLog(db, new TestClock(T0));
        string trace, span;
        using (var a = Source.StartActivity("step")!)
        {
            trace = a.TraceId.ToHexString();
            span = a.SpanId.ToHexString();
            await log.Append(id, "payment", new NewFact("step", "done", new JsonObject { ["step"] = "s1" }));
            await log.AppendMany(id, "payment", [new NewFact("step", "done", new JsonObject { ["step"] = "s2" }), new NewFact("step", "done", new JsonObject { ["step"] = "s3" })]);
        }
        // Outside a trace a fact names none: a fact of a host of an earlier release reads the same way.
        await log.Append(id, "payment", new NewFact("status", "ACSC"));
        db.ChangeTracker.Clear();
        var facts = await db.Facts.AsNoTracking().Where(f => f.SubjectId == id).OrderBy(f => f.Seq).ToListAsync();
        Assert.All(facts.Where(f => f.Kind == "step"), f => { Assert.Equal(trace, f.TraceId); Assert.Equal(span, f.SpanId); });
        Assert.Null(facts.Last().TraceId);
        // The ids are a correlation reference, not business data: the chain's hashes do not cover them.
        var step = facts.First(f => f.Kind == "step");
        var copy = new FactRow { PrevHash = step.PrevHash, SubjectId = step.SubjectId, SubjectKind = step.SubjectKind, Seq = step.Seq, Kind = step.Kind, Code = step.Code, DataJson = step.DataJson, At = step.At, ReleaseId = step.ReleaseId, Actor = step.Actor };
        Assert.Equal(step.Hash, FactLog.HashOf(copy));
        Assert.True((await log.Verify(id)).Ok);
        // Found by its trace (an index), and linked to the installation's trace view when one is configured.
        Assert.Equal(3, await db.Facts.CountAsync(f => f.TraceId == trace));
        var before = TraceLinks.Template;
        try
        {
            TraceLinks.Template = "https://traces.example.invalid/trace/{traceId}";
            Assert.Equal($"https://traces.example.invalid/trace/{trace}", TraceLinks.Of(trace));
            Assert.Null(TraceLinks.Of("not-a-trace-id"));
            TraceLinks.Template = null;
            Assert.Null(TraceLinks.Of(trace));
        }
        finally { TraceLinks.Template = before; }
    }

    [Theory, MemberData(nameof(Providers.Both), MemberType = typeof(Providers))]
    public async Task An_engines_failure_of_a_payment_is_a_fact_with_the_dictionarys_code(StoreProvider p)
    {
        await using var db = await dbs.Open(p);
        var id = await new PaymentStore(db, new TestClock(T0)).AcceptPayment("example-bank", "uae.p", Direction.Outward, Payment("E2E-T2"), "uae.p@1.0.0");
        foreach (var (engine, code) in new[] { ("temporal", "temporal.WorkflowTaskFailed"), ("dtfx", "dtfx.OrchestrationFailed"), ("elsa", "elsa.WorkflowFaulted"), ("flowable", "flowable.DeadLetterJob") })
        {
            var f = await EngineFailures.Record(db, id, engine, "root", "the workflow task failed: synthetic", new TestClock(T0), "dist/example-bank@2026.11.22-1");
            Assert.NotNull(f);
            Assert.Equal(("engine", "failed", $"engine:{engine}"), (f!.Kind, f.Code, f.Actor));
            var data = JsonNode.Parse(f.DataJson)!;
            Assert.Equal(code, (string?)data["code"]);
            Assert.Equal("dist/example-bank@2026.11.22-1", (string?)data["distribution"]);
        }
        // A subject the store does not hold gets nothing.
        Assert.Null(await EngineFailures.Record(db, "no-such-payment", "temporal", null, "x"));
        Assert.True((await new FactLog(db).Verify(id)).Ok);
    }
}
