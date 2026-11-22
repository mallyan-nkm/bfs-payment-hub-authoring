using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TransactHub.TestBench;

/// <summary>A run's result as a readable report: Markdown for a pull request or CI summary, HTML for a browser.</summary>
public static class Report
{
    static string Outcome(JsonObject r) => J.S(r, "error") is { } e ? $"error: {e.Split('\n')[0]}" : J.S(r, "outcome") ?? "none";

    static string StoreLine(JsonObject run)
    {
        if (run["store"] is not JsonObject st) return J.S(run, "database") == "none" ? "no store" : "none";
        var rows = st["rows"] as JsonObject;
        var bulks = (rows?["bulks"] as JsonArray)?.Count ?? 0;
        var payments = (rows?["payments"] as JsonArray)?.Count ?? 0;
        var payloads = (st["payloads"] as JsonArray)?.Count ?? 0;
        var chains = rows?["chains"] as JsonObject;
        return $"{J.S(st, "provider")}: {bulks} bulk(s), {payments} payment(s), {payloads} payload(s) rebuilt ({(st["ok"]?.GetValue<bool>() == true ? "all match" : "NOT the run")}), {chains?["verified"]}/{chains?["subjects"]} chains verified";
    }

    public static string Markdown(JsonObject r)
    {
        var sb = new StringBuilder();
        var plan = r["plan"] as JsonObject;
        sb.AppendLine($"# Test bench run {J.S(r, "id")}: {(r["passed"]?.GetValue<bool>() == true ? "PASSED" : "FAILED")}");
        sb.AppendLine();
        sb.AppendLine($"Profile `{J.S(plan, "profile")}`, workflow `{J.S(plan, "workflow")}`{(r["workflow"] is JsonObject w ? $"@{J.S(w, "release")}" : "")}, tenant `{J.S(plan, "tenant")}`, bench `{J.S(plan, "bench")}`, date {J.S(plan?["clock"], "date")}.");
        sb.AppendLine($"Input `{J.S(plan?["input"], "name")}` ({J.S(plan?["input"], "kind")}, {plan?["input"]?["records"]} record(s), {J.S(plan?["input"], "mode")}), sha256 `{J.S(plan?["input"], "sha256")?[..12]}…`. Content: {J.S(r, "content")}.");
        sb.AppendLine();
        sb.AppendLine("| Run | Database | Outcome | Same as the interpreter | Time | Store |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        if (r["reference"] is JsonObject re) sb.AppendLine($"| interpreter (reference) | | {Outcome(re)} | | {Runner.Ms(re["elapsedMs"])} | |");
        foreach (var run in (r["runs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var same = J.S(run, "error") is not null ? "no run" : run["comparison"]?["same"]?.GetValue<bool>() == true ? "yes" : "**NO**";
            sb.AppendLine($"| {J.S(run, "engine")} | {J.S(run, "database")} | {Outcome(run)} | {same} | {Runner.Ms(run["timings"]?["totalMs"])} | {StoreLine(run)} |");
        }
        sb.AppendLine();
        if (r["reference"] is JsonObject rr)
        {
            sb.AppendLine("## The interpreter's trace");
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var t in J.Strings(rr["trace"])) sb.AppendLine(t);
            sb.AppendLine("```");
            sb.AppendLine();
        }
        foreach (var run in (r["runs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (run["comparison"]?["trace"]?["diff"] is JsonArray d && d.Count > 0)
            {
                sb.AppendLine($"## {J.S(run, "engine")} on {J.S(run, "database")}: the trace differs (- interpreter, + engine)");
                sb.AppendLine();
                sb.AppendLine("```diff");
                foreach (var l in J.Strings(d)) sb.AppendLine(l);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }
        var failures = J.Strings(r["failures"]).ToList();
        sb.AppendLine(failures.Count == 0 ? "No failure." : "## Failures");
        foreach (var f in failures) sb.AppendLine($"- {f}");
        return sb.ToString();
    }

    static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    const string Css = """
            :root{--bg:#fff;--fg:#1b1f24;--muted:#5b6470;--line:#d8dee4;--ok:#1a7f37;--bad:#cf222e;--code:#f6f8fa}
            @media (prefers-color-scheme:dark){:root{--bg:#0d1117;--fg:#e6edf3;--muted:#8d96a0;--line:#30363d;--ok:#3fb950;--bad:#f85149;--code:#161b22}}
            body{background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,sans-serif;margin:0 auto;max-width:1200px;padding:16px}
            table{border-collapse:collapse;width:100%;margin:12px 0}td,th{border:1px solid var(--line);padding:4px 8px;text-align:left;vertical-align:top}
            .ok{color:var(--ok)}.bad{color:var(--bad)}pre{background:var(--code);padding:8px;overflow:auto;margin:0}.muted{color:var(--muted)}
            .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:8px}
            """;

    public static string Html(JsonObject r)
    {
        var plan = r["plan"] as JsonObject;
        var passed = r["passed"]?.GetValue<bool>() == true;
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Test bench run {{H(J.S(r, "id"))}}</title>
            <style>{{Css}}</style></head><body>
            <h1>Test bench run {{H(J.S(r, "id"))}}: <span class="{{(passed ? "ok" : "bad")}}">{{(passed ? "passed" : "failed")}}</span></h1>
            <p class="muted">Profile {{H(J.S(plan, "profile"))}} · {{H(J.S(plan, "workflow"))}} · tenant {{H(J.S(plan, "tenant"))}} · bench {{H(J.S(plan, "bench"))}} · input {{H(J.S(plan?["input"], "name"))}} ({{H(J.S(plan?["input"], "kind"))}})</p>
            <table><tr><th>Run</th><th>Database</th><th>Outcome</th><th>Same as the interpreter</th><th>Time</th><th>Store</th></tr>
            """);
        if (r["reference"] is JsonObject re) sb.Append($"<tr><td>interpreter (reference)</td><td></td><td>{H(Outcome(re))}</td><td></td><td>{Runner.Ms(re["elapsedMs"])}</td><td></td></tr>");
        foreach (var run in (r["runs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var same = run["comparison"]?["same"]?.GetValue<bool>() == true;
            sb.Append($"<tr><td>{H(J.S(run, "engine"))}</td><td>{H(J.S(run, "database"))}</td><td>{H(Outcome(run))}</td><td class=\"{(same ? "ok" : "bad")}\">{(J.S(run, "error") is not null ? "no run" : same ? "yes" : "no")}</td><td>{Runner.Ms(run["timings"]?["totalMs"])}</td><td>{H(StoreLine(run))}</td></tr>");
        }
        sb.Append("</table><h2>Traces side by side</h2><div class=\"grid\">");
        if (r["reference"] is JsonObject rr) sb.Append($"<div><b>interpreter</b><pre>{H(string.Join('\n', J.Strings(rr["trace"])))}</pre></div>");
        foreach (var run in (r["runs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var diff = run["comparison"]?["trace"]?["diff"] as JsonArray;
            var body = diff is { Count: > 0 } ? string.Join('\n', J.Strings(diff)) : string.Join('\n', J.Strings(run["trace"]));
            sb.Append($"<div><b>{H(J.S(run, "engine"))} · {H(J.S(run, "database"))}</b>{(diff is { Count: > 0 } ? " <span class=\"bad\">diff</span>" : "")}<pre>{H(body)}</pre></div>");
        }
        sb.Append("</div>");
        var failures = J.Strings(r["failures"]).ToList();
        sb.Append(failures.Count == 0 ? "<p class=\"ok\">No failure.</p>" : "<h2>Failures</h2><ul>" + string.Concat(failures.Select(f => $"<li class=\"bad\">{H(f)}</li>")) + "</ul>");
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    public static string SuiteMarkdown(JsonObject s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Test bench suite {J.S(s, "dir")}: {(s["passed"]?.GetValue<bool>() == true ? "PASSED" : "FAILED")}");
        sb.AppendLine();
        sb.AppendLine("| Case | Profile | Expected | Interpreter | Engines | Result |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var c in (s["cases"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var res = c["result"] as JsonObject;
            var engines = string.Join(", ", (res?["runs"] as JsonArray ?? []).OfType<JsonObject>().Select(x => $"{J.S(x, "engine")}/{J.S(x, "database")}: {Outcome(x)}"));
            sb.AppendLine($"| {J.S(c, "id")} {J.S(c, "title")} | {J.S(c, "profile")} | {J.S(c, "expected")} | {(res?["reference"] is JsonObject re ? Outcome(re) : "")} | {engines} | {(c["passed"]?.GetValue<bool>() == true ? (J.S(c, "known") is { } k ? $"pass (known: {k})" : "pass") : "**FAIL**")} |");
        }
        foreach (var c in (s["cases"] as JsonArray ?? []).OfType<JsonObject>().Where(c => c["passed"]?.GetValue<bool>() != true))
        {
            sb.AppendLine();
            sb.AppendLine($"## {J.S(c, "id")}");
            foreach (var f in J.Strings(c["result"]?["failures"])) sb.AppendLine($"- {f}");
        }
        return sb.ToString();
    }
}
