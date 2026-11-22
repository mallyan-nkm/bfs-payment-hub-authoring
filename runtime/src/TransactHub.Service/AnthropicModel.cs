using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace TransactHub.Service;

/// <summary>
/// The assistant's model on the Anthropic API, through the official C# SDK (as the studio's assistant uses the official
/// TypeScript one): the model <c>claude-opus-5-5</c>, adaptive thinking, streaming, the server-side fallback for a refusal,
/// and the tools streamed eagerly (their input is checked before a tool runs). The key is the studio's,
/// <c>TH_ANTHROPIC_API_KEY</c>. Only text the service has masked is given to it.
/// </summary>
public sealed class AnthropicAssistantModel(AnthropicClient client) : IAssistantModel
{
    public string Name => AssistantService.ModelId;

    public static AnthropicAssistantModel? FromEnvironment(Func<string, string?> env)
    {
        var key = env("TH_ANTHROPIC_API_KEY") ?? env("ANTHROPIC_API_KEY");
        return string.IsNullOrEmpty(key) ? null : new AnthropicAssistantModel(new AnthropicClient { ApiKey = key });
    }

    public IAssistantConversation Start(string system, IReadOnlyList<AssistantTool> tools) => new Conversation(client, system, tools);

    sealed class Conversation(AnthropicClient client, string system, IReadOnlyList<AssistantTool> tools) : IAssistantConversation
    {
        readonly List<BetaMessageParam> history = new();

        public Task<ModelReply> Ask(string userText, CancellationToken ct)
        {
            history.Add(new BetaUserMessageParam { Content = userText });
            return Next(ct);
        }

        public Task<ModelReply> Answer(IReadOnlyList<ToolOutcome> results, CancellationToken ct)
        {
            history.Add(new BetaUserMessageParam
            {
                Content = results.Select(r => (BetaContentBlockParam)new BetaToolResultBlockParam { ToolUseID = r.Id, Content = r.Content, IsError = r.IsError }).ToList(),
            });
            return Next(ct);
        }

        MessageCreateParams Params() => new()
        {
            Model = AssistantService.ModelId,
            MaxTokens = 64000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = Effort.High },
            System = system,
            Tools = tools.Select(t => (BetaToolUnion)new BetaTool
            {
                Name = t.Name,
                Description = t.Description,
                EagerInputStreaming = true,
                InputSchema = new()
                {
                    Properties = t.Properties.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
                    Required = t.Required,
                },
            }).ToList(),
            Messages = history.ToList(),
        };

        sealed class Block
        {
            public string Type = "";
            public string? Id, Name, Signature, Data;
            public readonly StringBuilder Text = new();
            public readonly StringBuilder Json = new();
        }

        async Task<ModelReply> Next(CancellationToken ct)
        {
            var blocks = new SortedDictionary<long, Block>();
            string stop = "end_turn";
            string? served = null;
            await foreach (var ev in client.Beta.Messages.CreateStreaming(Params(), ct))
            {
                if (ev.TryPickStart(out var start)) served = start.Message.Model;
                else if (ev.TryPickContentBlockStart(out var bs))
                {
                    var b = new Block();
                    if (bs.ContentBlock.TryPickBetaText(out var t)) { b.Type = "text"; b.Text.Append(t.Text); }
                    else if (bs.ContentBlock.TryPickBetaThinking(out var th)) { b.Type = "thinking"; b.Text.Append(th.Thinking); b.Signature = th.Signature; }
                    else if (bs.ContentBlock.TryPickBetaRedactedThinking(out var rt)) { b.Type = "redacted"; b.Data = rt.Data; }
                    else if (bs.ContentBlock.TryPickBetaToolUse(out var tu)) { b.Type = "tool"; b.Id = tu.ID; b.Name = tu.Name; }
                    else b.Type = "other";
                    blocks[bs.Index] = b;
                }
                else if (ev.TryPickContentBlockDelta(out var bd) && blocks.TryGetValue(bd.Index, out var cur))
                {
                    if (bd.Delta.TryPickText(out var td)) cur.Text.Append(td.Text);
                    else if (bd.Delta.TryPickInputJson(out var jd)) cur.Json.Append(jd.PartialJson);
                    else if (bd.Delta.TryPickThinking(out var thd)) cur.Text.Append(thd.Thinking);
                    else if (bd.Delta.TryPickSignature(out var sd)) cur.Signature = (cur.Signature ?? "") + sd.Signature;
                }
                else if (ev.TryPickDelta(out var md) && md.Delta.StopReason is { } sr) stop = sr.ToString() ?? stop;
            }
            stop = Wire(stop);
            // The assistant's turn goes back into the history as it came, thinking and its signature unchanged.
            var content = new List<BetaContentBlockParam>();
            var calls = new List<ToolCall>();
            foreach (var b in blocks.Values)
            {
                switch (b.Type)
                {
                    case "text": if (b.Text.Length > 0) content.Add(new BetaTextBlockParam { Text = b.Text.ToString() }); break;
                    case "thinking": content.Add(new BetaThinkingBlockParam { Thinking = b.Text.ToString(), Signature = b.Signature ?? "" }); break;
                    case "redacted": content.Add(new BetaRedactedThinkingBlockParam { Data = b.Data ?? "" }); break;
                    case "tool":
                        JsonObject input;
                        // Eager input streaming: the API no longer validates the input, so a truncated or invalid one is refused here.
                        try { input = JsonNode.Parse(b.Json.Length == 0 ? "{}" : b.Json.ToString()) as JsonObject ?? throw new JsonException(); }
                        catch (JsonException) { input = new JsonObject { ["__invalid"] = true }; }
                        content.Add(new BetaToolUseBlockParam { ID = b.Id!, Name = b.Name!, Input = input.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)) });
                        if (stop is not ("max_tokens" or "refusal")) calls.Add(new ToolCall(b.Id!, b.Name!, input));
                        break;
                }
            }
            if (content.Count > 0) history.Add(new BetaAssistantMessageParam { Content = content });
            var text = string.Concat(blocks.Values.Where(b => b.Type == "text").Select(b => b.Text.ToString()));
            if (stop == "refusal") text = "The model declined to answer this request. Use the runbooks directly.";
            // An input that is not valid for its tool still gets a tool result: an error the model can correct.
            return new ModelReply(text, calls.Select(c => Valid(c) ? c : c with { Input = new JsonObject { ["__invalid"] = true } }).ToList(), stop, served);
        }

        bool Valid(ToolCall c)
        {
            var t = tools.FirstOrDefault(x => x.Name == c.Name);
            return t is not null && !c.Input.ContainsKey("__invalid") && t.Required.All(c.Input.ContainsKey) && c.Input.All(kv => t.Properties.ContainsKey(kv.Key) && kv.Value is JsonValue);
        }

        static string Wire(string s) => s.ToLowerInvariant() switch
        {
            var x when x.Contains("tool") => "tool_use",
            var x when x.Contains("max") => "max_tokens",
            var x when x.Contains("refusal") => "refusal",
            var x when x.Contains("pause") => "pause_turn",
            _ => "end_turn",
        };
    }
}
