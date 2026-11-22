using System.Text.Json.Nodes;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.DependencyInjection;
using TransactHub.Engines;

namespace ElsaHarness;

/// <summary>
/// The one generic Elsa activity: it interprets the expanded DSL document named in its journal
/// (Part 9, interpreter strategy). The journal lives in the workflow's own properties, so it is
/// serialised with the workflow state Elsa keeps. While the instance waits on anything, the activity
/// holds a bookmark; the host resumes that bookmark with the results that have arrived.
/// </summary>
public sealed class InterpreterActivity : Activity
{
    public const string Stimulus = "transacthub:wake";
    public const string JournalKey = "transacthub";
    public static Deployment Dep = null!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var j = (JsonObject)J.Parse((string)context.WorkflowInput["journal"])!;
        await Step(context, j);
    }

    async ValueTask OnResume(ActivityExecutionContext context)
    {
        var j = (JsonObject)J.Parse((string)context.WorkflowExecutionContext.Properties[JournalKey])!;
        var entries = (JsonArray)J.Parse((string)context.WorkflowInput["entries"])!;
        var all = (JsonArray)j["entries"]!;
        foreach (var e in entries) all.Add(J.Clone(e));
        await Step(context, j);
    }

    async ValueTask Step(ActivityExecutionContext context, JsonObject j)
    {
        Replay.Episode(j, Dep);
        context.WorkflowExecutionContext.Properties[JournalKey] = J.Str(j);
        if (J.Truthy(J.P(j, "done"))) await context.CompleteActivityAsync();
        else context.CreateBookmark(new CreateBookmarkArgs { Callback = OnResume, Stimulus = Stimulus, IncludeActivityInstanceId = false });
    }
}

/// <summary>Elsa's workflow runner and state serialiser. Each run starts from the serialised state.</summary>
public sealed class ElsaHost
{
    readonly IWorkflowRunner runner;
    readonly IWorkflowStateSerializer serializer;

    public ElsaHost()
    {
        var services = new ServiceCollection();
        services.AddElsa();
        var sp = services.BuildServiceProvider();
        runner = sp.GetRequiredService<IWorkflowRunner>();
        serializer = sp.GetRequiredService<IWorkflowStateSerializer>();
    }

    /// <summary>The distribution this host runs (R-09-54): the definition carries it, one definition per distribution.</summary>
    public static string? Distribution;

    static Workflow Definition() => Distribution is null
        ? new() { Root = new InterpreterActivity() }
        : new() { Identity = new Elsa.Workflows.Models.WorkflowIdentity($"transacthub.interpreter@{Distribution}", 1, $"transacthub.interpreter@{Distribution}"), Root = new InterpreterActivity() };

    public sealed record Result(string State, JsonObject Journal, string Status);

    /// <summary>Start a new workflow instance (one execution of one engine instance).</summary>
    public Result Start(string elsaInstanceId, JsonObject journal)
    {
        var r = runner.RunAsync(Definition(), new RunWorkflowOptions
        {
            WorkflowInstanceId = elsaInstanceId,
            Input = new Dictionary<string, object> { ["journal"] = J.Str(journal) },
        }).GetAwaiter().GetResult();
        return Read(r.WorkflowState);
    }

    /// <summary>Resume a suspended instance from its serialised state, with new journal entries.</summary>
    public Result Resume(string state, JsonArray entries)
    {
        var st = serializer.Deserialize(state);
        var bookmark = st.Bookmarks.SingleOrDefault(b => b.Name == InterpreterActivity.Stimulus || st.Bookmarks.Count == 1)
            ?? throw new InvalidOperationException($"{st.Id} has no bookmark to resume");
        var r = runner.RunAsync(Definition(), st, new RunWorkflowOptions
        {
            BookmarkId = bookmark.Id,
            Input = new Dictionary<string, object> { ["entries"] = J.Str(entries) },
        }).GetAwaiter().GetResult();
        return Read(r.WorkflowState);
    }

    Result Read(Elsa.Workflows.State.WorkflowState st)
    {
        if (st.SubStatus == WorkflowSubStatus.Faulted)
        {
            var why = string.Join("; ", st.Incidents.Select(i => $"{i.ActivityId}: {i.Message}"));
            throw new InvalidOperationException($"Elsa faulted {st.Id}: {why}");
        }
        var journal = (JsonObject)J.Parse(st.Properties[InterpreterActivity.JournalKey]?.ToString())!;
        return new Result(serializer.Serialize(st), journal, $"{st.Status}/{st.SubStatus}");
    }
}
