using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Store;

namespace TransactHub.Service.Tests;

/// <summary>
/// The assistant (R-09-44), on a fake model that records every byte it is given (no real API call is made): it is off unless
/// switched on; no party value of the store reaches the model (names, IBANs, accounts, employee and employer ids); it uses
/// read-only tools, cites its sources, and proposes an action with its runbook without carrying it out.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AssistantTests(Databases dbs)
{
    sealed class FakeModel(Func<int, string, ModelReply> script) : IAssistantModel
    {
        public readonly List<string> Sent = new();
        public string Name => "fake";
        public IAssistantConversation Start(string system, IReadOnlyList<AssistantTool> tools)
        {
            Sent.Add(system);
            Sent.AddRange(tools.Select(t => t.Description));
            return new Conversation(this);
        }

        sealed class Conversation(FakeModel m) : IAssistantConversation
        {
            int turn;
            public Task<ModelReply> Ask(string userText, CancellationToken ct) { m.Sent.Add(userText); return Task.FromResult(m.Next(turn++, userText)); }
            public Task<ModelReply> Answer(IReadOnlyList<ToolOutcome> results, CancellationToken ct)
            {
                foreach (var r in results) m.Sent.Add(r.Content);
                return Task.FromResult(m.Next(turn++, string.Join("\n", results.Select(r => r.Content))));
            }
        }

        ModelReply Next(int turn, string seen) => script(turn, seen);
    }

    static ToolCall Call(string name, object input) => new(Guid.NewGuid().ToString("N"), name, (JsonObject)JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(input))!);

    [Fact]
    public async Task It_is_off_unless_switched_on()
    {
        using var s = new Signers();
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), model: new FakeModel((_, _) => new ModelReply("x", [], "end_turn", "fake")));
        Assert.False((bool)(await J.Of(await api.Engineer.GetAsync("/service/api/assistant")))["enabled"]!);
        var r = await api.Engineer.PostAsJsonAsync("/service/api/assistant", new { question = "Why is the file held?" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("assistant.Disabled", (string?)(await J.Of(r))["code"]);
    }

    [Fact]
    public async Task No_party_value_leaves_the_runtime_its_tools_only_read_and_it_proposes_an_action_with_its_runbook()
    {
        using var s = new Signers();
        string bulk = "";
        var model = new FakeModel((turn, _) => turn switch
        {
            0 => new ModelReply("", [Call("get_payment_trace", new { paymentId = bulk }), Call("lookup_error", new { code = "AM02", scheme = "uae.wps" }), Call("recent_errors", new { tenant = Seed.Tenant }),
                Call("get_health", new { }), Call("get_configuration", new { }), Call("search_docs", new { query = "hold approval operator" }), Call("lookup_error", new { nope = 1 })], "tool_use", "fake"),
            1 => new ModelReply("", [Call("propose_action", new { action = "release-hold", target = bulk, runbook = "AM02", why = "The amount is within the agreed limit" })], "tool_use", "fake"),
            _ => new ModelReply($"The file is held for an amount above the bank's limit [trace:{bulk}] [error:AM02]. An approver may release the hold [doc:design/operations-console.md].", [], "end_turn", "fake"),
        });
        await using var api = new ServiceApi(dbs.Fresh(), s, new FakeGitHub(), new() { ["TH_ASSISTANT_ENABLED"] = "true" }, model);
        var se = api.Engineer;
        bulk = await Seed.Bulk(api.Store, "p.wf.corporate.v1");
        // An operator's free-text remark can name anyone: it is never sent.
        await using (var db = StoreDbContext.Open(api.Store))
            await new FactLog(db).Append(bulk, "bulk", new NewFact("operator", "requested", new JsonObject { ["action"] = "a1", ["name"] = "approve", ["remarks"] = "Called Synthetic Remark Person about it" }, Actor: "operator:op-1"));
        await api.CatchUp();
        await using (var db = StoreDbContext.Open(api.Store)) Assert.True(await db.Parties.AnyAsync(p => p.Name == Seed.PartyName));
        var before = await Facts(api.Store);

        var r = await se.PostAsJsonAsync("/service/api/assistant", new { question = $"Why is the file of {Seed.PartyName} held? The IBAN is {Seed.Iban}.", paymentId = bulk });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var a = await J.Of(r);

        // Nothing the model was given holds a party value.
        var sent = string.Join("\n", model.Sent);
        Assert.Contains("[masked]", sent);
        Assert.Contains(bulk, sent);
        foreach (var v in new[] { Seed.PartyName, Seed.CreditorName, Seed.Iban, Seed.PersonId, Seed.EmployerId, "AE070331234567890123999", "Synthetic Remark Person" })
            Assert.DoesNotContain(v, sent, StringComparison.OrdinalIgnoreCase);
        // Grounded: the trace, the dictionary's runbook and a document reached the model.
        Assert.Contains("internal-limit", sent);
        Assert.Contains("\"safeActions\"", sent);
        Assert.Contains("[doc:", sent);
        Assert.Contains("INVALID_JSON", sent);

        Assert.Equal([$"trace:{bulk}", "error:AM02", "doc:design/operations-console.md"], a["cited"]!.AsArray().Select(x => (string)x!));
        Assert.Contains("health", a["sources"]!.AsArray().Select(x => (string?)x));
        var p = a["proposals"]!.AsArray().Single()!;
        Assert.Equal("release-hold", (string?)p["action"]);
        Assert.Equal("#/service/errors/AM02", (string?)p["runbookLink"]);
        Assert.True((bool)p["inRunbook"]!);
        Assert.True((bool)p["risky"]!);
        // It changed nothing: no fact was written, the hold is still open.
        Assert.Equal(before, await Facts(api.Store));
        Assert.Equal("fake", (string?)a["model"]);
    }

    static async Task<int> Facts(StoreOptions o)
    {
        await using var db = StoreDbContext.Open(o);
        return await db.Facts.CountAsync();
    }
}
