using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Operations;
using TransactHub.Store;

namespace TransactHub.FileApi;

/// <summary>
/// The orders a reconciliation break starts (R-09-250; owner's decision of 24 November 2026, Issue I-168): each break a bank approver
/// consented to resolve by an exception request or a return goes in through the File API's own intake, as the message the scheme's
/// report names for it, with the break's order document (<see cref="BreakOrder"/>) as its file: checked and kept once like every file,
/// then started by the front door, whose dispatch table routes it (an exception request on <c>p.wf.request.v1</c>, a return on the
/// workflow its kind selects). What became of it is a fact on the run's chain: <c>order-started</c> with the payment, or
/// <c>order-refused</c> with the refusal's code. Each break's order is handled once: a pass reads from the facts what is left.
/// </summary>
public sealed class BreakOrderStarter(StoreOptions store, FileIntake intake, string environment, string? bank = null, TimeProvider? clock = null)
{
    public const string Channel = "reconciliation";

    /// <summary>Starts every order consented to and not yet handled. Returns how many it handled.</summary>
    public async Task<int> StartPending(CancellationToken ct = default)
    {
        var n = 0;
        foreach (var (run, data, resolved) in await BreakOrder.Pending(store, bank, ct))
        {
            var id = (string)data["break"]!;
            var message = (string?)resolved["order"]?["message"];
            if (message is not { Length: > 0 })
            {
                await BreakOrder.Record(store, run, id, false, new JsonObject { ["code"] = BreakQueue.NoOrderMessage, ["why"] = "The resolution names no message" }, clock, ct);
                n++;
                continue;
            }
            var doc = BreakOrder.Of(run, data, resolved);
            var content = Encoding.UTF8.GetBytes(doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var r = await intake.Receive(new Arrival((string?)data["tenant"] ?? "", environment, message, $"{id}.json", content, Channel, "file-api:reconcile"), ct);
            if (r.Accepted && r.FrontDoor?.PaymentId is { } payment)
                await BreakOrder.Record(store, run, id, true, new JsonObject { ["payment"] = payment, ["file"] = r.FileId, ["message"] = message, ["workflow"] = r.FrontDoor.Workflow }, clock, ct);
            else
                await BreakOrder.Record(store, run, id, false, new JsonObject { ["code"] = r.Code ?? "frontdoor.NotRouted", ["why"] = r.Detail, ["file"] = r.FileId, ["message"] = message }, clock, ct);
            n++;
        }
        return n;
    }
}
