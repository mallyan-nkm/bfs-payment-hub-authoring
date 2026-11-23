using System.Text.Json.Nodes;
using TransactHub.FileApi;

namespace TransactHub.FileApi.Tests;

/// <summary>
/// The runtime's front door routes every start message of the content branch's schemes as the studio deploy runner's front door does:
/// both are held to one fixture, front-door-routes.json, which studio/test/front-door-route.test.ts reads too (R-09-79).
/// </summary>
public sealed class FrontDoorRouteTests
{
    static readonly DateOnly On = new(2026, 11, 23);

    static Dispatch Content() => Dispatch.Of(Dispatch.DocumentsOfFolder(Path.Combine(SftpTests.Repo(), "content-configurations", "artefacts")));

    public static JsonObject Routes(Dispatch d)
    {
        var o = new JsonObject();
        foreach (var s in d.Schemes.OrderBy(s => Dispatch.Str(s, "scope"), StringComparer.Ordinal))
            foreach (var m in (s["messages"] as JsonArray ?? []).OfType<JsonObject>().Where(m => m["disposition"]?["start"] is not null).OrderBy(m => Dispatch.Str(m, "id"), StringComparer.Ordinal))
            {
                var r = d.Route(Dispatch.Str(m, "id"), On);
                o[r.Message] = new JsonObject
                {
                    ["scheme"] = r.Scheme, ["intakeMap"] = r.IntakeMap, ["route"] = r.Route.DeepClone(), ["workflow"] = r.Workflow,
                    ["legs"] = new JsonObject(r.Legs.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value))),
                };
            }
        return o;
    }

    [Fact]
    public void Every_start_message_routes_as_the_fixture_says()
    {
        var actual = Routes(Content());
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "front-door-routes.json")))!["routes"]!;
        Assert.True(JsonNode.DeepEquals(expected, actual), $"The front door's routes are:\n{actual.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}");
    }

    [Fact]
    public void Workflow_select_refuses_what_it_cannot_read_and_a_message_that_is_not_a_start()
    {
        var d = Content();
        Assert.Equal("p.wf.inward.v1", d.Select(new JsonObject { ["kind"] = "payment", ["direction"] = "inward" }, On));
        Assert.Null(d.Select(new JsonObject { ["kind"] = "nothing" }, On));
        Assert.Equal("frontdoor.NotAStart", Assert.Throws<FrontDoorRefused>(() => d.Route("uae.wps.psi", On)).Code);
        Assert.Equal("frontdoor.UnknownMessage", Assert.Throws<FrontDoorRefused>(() => d.Route("uae.wps.none", On)).Code);
        Assert.Equal("deliver", d.Entry("uae.wps.ack")!.Kind);
        Assert.Equal("sent", d.Entry("uae.wps.prc")!.Kind);
        var table = (JsonObject)d.Document(Dispatch.WorkflowSelect)!.DeepClone();
        ((JsonObject)((JsonArray)table["rows"]!)[0]!["when"]!)["kind"] = new JsonObject { ["in"] = new JsonArray("a") };
        var bad = Dispatch.Of([.. d.Schemes, table]);
        Assert.Equal("frontdoor.TableNotSupported", Assert.Throws<FrontDoorRefused>(() => bad.Select(new JsonObject { ["kind"] = "request" }, On)).Code);
        Assert.Equal("frontdoor.Ambiguous", new FrontDoorRefused("frontdoor.Ambiguous", "x").Code);
        Assert.Equal("000000000001", RuntimeFrontDoor.Stem("000000000001.PIF.gpg"));
    }
}
