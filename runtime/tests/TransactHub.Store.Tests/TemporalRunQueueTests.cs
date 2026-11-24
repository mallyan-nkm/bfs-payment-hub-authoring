using TransactHub.Engine.Temporal;
using Xunit;

namespace TransactHub.Store.Tests;

// Issue I-331, R-09-216: on a shared Temporal server (the laptop's), every test bench run polled the plan's bare task queues
// (transacthub, th-p, th-uae-wps, ...), the same names as another bench run's and the engine host's long-running workers. Two
// runs at once took each other's tasks: the bank's run had its file checks run by the other run's harness, which held no payload
// for it, and ended rejected (FF01) before its salary file was ever sent, while the interpreter settled.

public sealed class TemporalRunQueueTests
{
    static readonly string[] PlanQueues = [ScenarioRunner.HostQueue, "th-p", "th-uae-p", "th-uae-wps", "th-bank"];

    [Fact]
    public void Two_runs_of_one_plan_on_one_server_poll_no_task_queue_in_common()
    {
        var a = PlanQueues.Select(q => ScenarioRunner.RunQueue(q, null, "0a1b2c3d")).ToHashSet();
        var b = PlanQueues.Select(q => ScenarioRunner.RunQueue(q, null, "4e5f6a7b")).ToHashSet();
        Assert.Empty(a.Intersect(b));
    }

    [Fact]
    public void A_run_polls_none_of_the_queues_the_engine_hosts_long_running_workers_poll()
    {
        // The engine host's workers poll the plan's own names, its distributions told apart by build id (DistributionWorkers).
        var run = PlanQueues.Select(q => ScenarioRunner.RunQueue(q, null, "0a1b2c3d")).ToHashSet();
        Assert.Empty(run.Intersect(PlanQueues));
    }

    [Fact]
    public void A_banks_distribution_keeps_its_own_queues_as_before()
    {
        // R-09-55: a distribution's copy carries the distribution, whatever the run.
        Assert.Equal("th-uae-wps@adib-1.2.0", ScenarioRunner.RunQueue("th-uae-wps", "adib-1.2.0", "0a1b2c3d"));
        Assert.Equal("th-uae-wps@adib-1.2.0", ScenarioRunner.RunQueue("th-uae-wps", "adib-1.2.0", "4e5f6a7b"));
    }
}
