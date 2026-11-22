using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TransactHub.TestBench;

/// <summary>
/// The data of a run. A scheme file (a WPS SIF or PIF) goes in through the front door as its start message. An ISO 20022
/// message (pain.001, pacs.008) or a canonical JSON payment becomes the workflow's context: no scheme in the factory takes
/// pain.001 or pacs.008 as a start message (Issue I-95), so those run the platform workflow from the context, on every
/// engine and the interpreter alike.
/// </summary>
public sealed record Input(string Kind, string Name, string? Payload, JsonObject? Context, string? Message, string Sha256, int Records)
{
    public JsonObject Summary() => new() { ["kind"] = Kind, ["name"] = Name, ["message"] = Message, ["sha256"] = Sha256, ["records"] = Records, ["mode"] = Payload is not null ? "front door" : "context" };
}

public static class Inputs
{
    public static Input Read(JsonObject data, Library library, string tenant)
    {
        string text, name;
        if (J.S(data, "sample") is { } id)
        {
            (text, name) = library.Payload(id, tenant);
        }
        else if (J.S(data, "file") is { } file)
        {
            if (!File.Exists(file)) throw new BenchError($"No file {file}");
            text = File.ReadAllText(file);
            name = Path.GetFileName(file);
        }
        else if (J.S(data, "base64") is { } b64)
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            name = J.S(data, "name") ?? "upload";
        }
        else if (J.S(data, "text") is { } t)
        {
            text = t;
            name = J.S(data, "name") ?? "pasted";
        }
        else if (data["build"] is JsonObject recipe)
        {
            var r = (JsonObject)recipe.DeepClone();
            r["tenant"] ??= tenant;
            var built = new DataBuilder(library.Home).Build(r);
            (text, name) = (built.Text, built.Name);
        }
        else if (data["context"] is JsonObject ctx)
        {
            text = ctx.ToJsonString();
            name = J.S(data, "name") ?? "context.json";
        }
        else throw new BenchError("data names a file, text, base64, a sample or a context");
        return Detect(text, name, J.S(data, "kind"));
    }

    public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static Input Detect(string text, string name, string? kind = null)
    {
        var t = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        kind ??= t.StartsWith('{') ? "json"
            : t.StartsWith('<') ? (t.Contains("pain.001", StringComparison.Ordinal) ? "pain.001" : t.Contains("pacs.008", StringComparison.Ordinal) ? "pacs.008" : throw new BenchError($"{name}: an XML message the test bench does not read (pain.001 and pacs.008 are read)"))
            : lines.Any(l => l.StartsWith("SCR,")) ? "wps-sif"
            : lines.Any(l => l.StartsWith("PCR,")) ? "wps-pif"
            : throw new BenchError($"{name}: not a WPS SIF or PIF, an ISO 20022 pain.001 or pacs.008, or a canonical JSON payment");
        var sha = Sha(text);
        return kind switch
        {
            "wps-sif" => new Input(kind, name, Normalise(text), null, "uae.wps.sif", sha, lines.Count(l => l.StartsWith("EDR,"))),
            "wps-pif" => new Input(kind, name, Normalise(text), null, "uae.wps.pif", sha, lines.Count(l => l.StartsWith("PDR,"))),
            "json" => Json(text, name, sha),
            "pain.001" or "pacs.008" => Iso(text, name, kind, sha),
            _ => throw new BenchError($"No input kind {kind}"),
        };
    }

    static string Normalise(string text) => text.Replace("\r\n", "\n");

    static Input Json(string text, string name, string sha)
    {
        JsonObject o;
        try { o = JsonNode.Parse(text) as JsonObject ?? throw new BenchError($"{name}: not a JSON object"); }
        catch (System.Text.Json.JsonException e) { throw new BenchError($"{name}: not JSON: {e.Message}"); }
        var ctx = o["context"] as JsonObject ?? o;
        if (ctx["header"] is not JsonObject) throw new BenchError($"{name}: a canonical payment has a header (kind, direction, segment)");
        return new Input("json", name, null, (JsonObject)ctx.DeepClone(), null, sha, (ctx["items"] as JsonArray)?.Count ?? 1);
    }

    static XElement? E(XElement? e, params string[] path)
    {
        foreach (var p in path) e = e?.Elements().FirstOrDefault(x => x.Name.LocalName == p);
        return e;
    }

    static IEnumerable<XElement> All(XElement e, string name) => e.Descendants().Where(x => x.Name.LocalName == name);
    static string? V(XElement? e, params string[] path) => E(e, path)?.Value.Trim();
    static decimal Dec(string? s) => decimal.Parse(s ?? "0", CultureInfo.InvariantCulture);

    static JsonObject Agent(XElement? agt)
    {
        var o = new JsonObject();
        if (V(agt, "FinInstnId", "ClrSysMmbId", "MmbId") is { } m) o["clearingMemberId"] = m;
        if (V(agt, "FinInstnId", "BICFI") is { } b) o["bic"] = b;
        return o;
    }

    static JsonObject Account(XElement? acct) => V(acct, "Id", "IBAN") is { } iban ? new JsonObject { ["iban"] = iban } : new JsonObject { ["id"] = V(acct, "Id", "Othr", "Id") };

    /// <summary>
    /// An ISO 20022 message as the canonical context: ids, amounts, accounts and agents. Party names and addresses are not
    /// carried: personal data never enters workflow state (spec/CLAUDE.md, Data).
    /// </summary>
    static Input Iso(string text, string name, string kind, string sha)
    {
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (System.Xml.XmlException e) { throw new BenchError($"{name}: not XML: {e.Message}"); }
        var root = doc.Root!;
        var hdr = All(root, "GrpHdr").FirstOrDefault() ?? throw new BenchError($"{name}: no GrpHdr");
        var msgId = V(hdr, "MsgId") ?? "MSG-1";
        var txs = All(root, "CdtTrfTxInf").ToList();
        if (txs.Count == 0) throw new BenchError($"{name}: no CdtTrfTxInf");
        JsonObject ctx;
        if (kind == "pain.001")
        {
            var pmt = All(root, "PmtInf").First();
            var items = new JsonArray();
            string? ccy = null;
            foreach (var tx in txs)
            {
                var amt = E(tx, "Amt", "InstdAmt");
                ccy ??= amt?.Attribute("Ccy")?.Value;
                items.Add(new JsonObject
                {
                    ["payment"] = new JsonObject
                    {
                        ["endToEndId"] = V(tx, "PmtId", "EndToEndId"),
                        ["instructedAmount"] = new JsonObject { ["value"] = Dec(amt?.Value), ["currency"] = amt?.Attribute("Ccy")?.Value },
                        ["creditorAgent"] = Agent(E(tx, "CdtrAgt")), ["creditorAccount"] = Account(E(tx, "CdtrAcct")),
                    },
                });
            }
            var sum = V(hdr, "CtrlSum") is { } cs ? Dec(cs) : txs.Sum(t => Dec(V(t, "Amt", "InstdAmt")));
            ctx = new JsonObject
            {
                ["header"] = new JsonObject { ["paymentId"] = V(pmt, "PmtInfId") ?? "P-1", ["endToEndId"] = V(txs[0], "PmtId", "EndToEndId") ?? msgId, ["fileId"] = msgId, ["kind"] = "payment", ["direction"] = "outward", ["segment"] = "corporate" },
                ["payment"] = new JsonObject { ["instructedAmount"] = new JsonObject { ["value"] = sum, ["currency"] = ccy }, ["debtorAccount"] = Account(E(pmt, "DbtrAcct")), ["debtorAgent"] = Agent(E(pmt, "DbtrAgt")) },
                ["batch"] = new JsonObject { ["id"] = msgId, ["numberOfTransactions"] = int.Parse(V(hdr, "NbOfTxs") ?? txs.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), ["controlSum"] = sum, ["currency"] = ccy, ["categoryPurpose"] = V(pmt, "PmtTpInf", "CtgyPurp", "Cd"), ["debtorAgent"] = Agent(E(pmt, "DbtrAgt")) },
                ["items"] = items,
            };
        }
        else
        {
            var tx = txs[0];
            var amt = E(tx, "IntrBkSttlmAmt");
            ctx = new JsonObject
            {
                ["header"] = new JsonObject { ["paymentId"] = V(tx, "PmtId", "TxId") ?? "P-1", ["endToEndId"] = V(tx, "PmtId", "EndToEndId") ?? msgId, ["fileId"] = msgId, ["kind"] = "payment", ["direction"] = "inward", ["segment"] = "commercial" },
                ["payment"] = new JsonObject
                {
                    ["instructedAmount"] = new JsonObject { ["value"] = Dec(amt?.Value), ["currency"] = amt?.Attribute("Ccy")?.Value },
                    ["settlementDate"] = V(hdr, "IntrBkSttlmDt") ?? V(tx, "IntrBkSttlmDt"),
                    ["debtorAccount"] = Account(E(tx, "DbtrAcct")), ["debtorAgent"] = Agent(E(tx, "DbtrAgt")),
                    ["creditorAccount"] = Account(E(tx, "CdtrAcct")), ["creditorAgent"] = Agent(E(tx, "CdtrAgt")),
                },
            };
        }
        return new Input(kind, name, null, ctx, null, sha, txs.Count);
    }
}

/// <summary>The sample library (runtime/testbench/samples/library.yaml, and samples saved under local/samples): ready synthetic data, tagged.</summary>
public sealed class Library(Home home)
{
    public Home Home => home;

    public IEnumerable<JsonObject> Entries()
    {
        var shipped = Yaml.Load(Path.Combine(home.Samples, "library.yaml"))["samples"] as JsonArray ?? [];
        foreach (var e0 in shipped.OfType<JsonObject>()) { var e = (JsonObject)e0.DeepClone(); e["origin"] = "shipped"; yield return e; }
        if (Directory.Exists(home.SavedSamples))
            foreach (var f in Directory.EnumerateFiles(home.SavedSamples, "*.yaml").Order(StringComparer.Ordinal))
            {
                var e = Yaml.Load(f);
                e["origin"] = "saved";
                yield return e;
            }
    }

    public JsonObject Entry(string id) => Entries().FirstOrDefault(e => J.S(e, "id") == id)
        ?? throw new BenchError($"No sample {id} in the library (testbench data list)");

    /// <summary>The sample's data: its file, or what its recipe builds for the tenant.</summary>
    public (string Text, string Name) Payload(string id, string tenant)
    {
        var e = Entry(id);
        if (J.S(e, "file") is { } f)
        {
            var path = J.S(e, "origin") == "saved" ? Path.Combine(home.SavedSamples, f) : Path.Combine(home.Samples, f);
            return (File.ReadAllText(path), Path.GetFileName(f));
        }
        if (J.S(e, "text") is { } t) return (t, J.S(e, "name") ?? id);
        if (e["build"] is JsonObject recipe)
        {
            var r = (JsonObject)recipe.DeepClone();
            r["tenant"] ??= tenant;
            var built = new DataBuilder(home).Build(r);
            return (built.Text, built.Name);
        }
        throw new BenchError($"The sample {id} has no file, text or build recipe");
    }
}
