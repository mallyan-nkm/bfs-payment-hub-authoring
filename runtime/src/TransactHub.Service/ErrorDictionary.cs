using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TransactHub.Api;
using TransactHub.Operations;
using TransactHub.Security;
using TransactHub.Store;

namespace TransactHub.Service;

/// <summary>
/// The runtime's own error codes (spec R-05-51): what the store, signing, the package source, authentication, the consoles
/// and the assistant raise. Each has an entry in the platform's error dictionary (<c>p.errors.platform.v1</c>);
/// <c>ErrorDictionaryTests</c> fails on one that has none.
/// </summary>
public static class ErrorCodes
{
    public const string InsertOnly = "store.InsertOnlyViolation", ChainBroken = "store.ChainBroken", ReleaseConflict = "store.ReleaseConflict", StoreUnavailable = "store.Unavailable";
    public const string ReadModelLagging = "readmodel.Lagging", ReadModelGap = "readmodel.Gap";
    public const string NotSigned = "signing.NotSigned", Tampered = "signing.Tampered", UntrustedKey = "signing.UntrustedKey", RevokedKey = "signing.RevokedKey",
        RetiredKey = "signing.RetiredKey", WrongRelease = "signing.WrongRelease", TrustListInvalid = "signing.TrustListInvalid";
    public const string PackageRejected = "package.Rejected", NotPromotable = "package.NotPromotable", KindNotInstallable = "package.KindNotInstallable";
    public const string SourceUnreachable = "package-source.Unreachable", UnknownVersion = "package-source.UnknownVersion";
    public const string Unauthenticated = "auth.Unauthenticated", Forbidden = "auth.Forbidden", StudioIssuer = "auth.StudioIssuer", DevRefused = "auth.DevRefused", RoleMapInvalid = "auth.RoleMapInvalid";
    public const string ActionNotOffered = "ops.ActionNotOffered", RemarksRequired = "ops.RemarksRequired", MakerApproves = "ops.MakerApproves",
        AlreadyDecided = "ops.AlreadyDecided", HoldClosed = "ops.HoldClosed", NoEnginePath = "ops.NoEnginePath";
    // The refusals of an action that does not apply to the payment now, each with its own code since the dictionary's 1.1.0
    // (Issue I-126): no parked step, no kept file, no posting reference, no such payment; a worker that does not answer its
    // control; a paused intake; the installation's log back end that does not answer (I-103).
    public const string NoParkedStep = "ops.NoParkedStep", NoFileKept = "ops.NoFileKept", PostingUnknown = "ops.NoPostingReference", NotFound = "ops.UnknownPayment",
        WorkerUnreachable = "ops.WorkerUnreachable", IntakePaused = "ops.IntakePaused", LogSourceUnreachable = "service.LogSourceUnreachable";
    public const string ApproverRequired = "service.ApproverRequired", RemovalRefused = "service.RemovalRefused", RestoreRefused = "service.RestoreRefused",
        BackupInvalid = "service.BackupInvalid", ProbeFailed = "service.ProbeFailed", CertificateExpiring = "service.CertificateExpiring", DiskLow = "service.DiskLow";
    public const string AssistantDisabled = "assistant.Disabled", AssistantUnavailable = "assistant.Unavailable";
    public const string SecretUnavailable = "SecretUnavailable";

    /// <summary>Every runtime code above, for the dictionary test.</summary>
    public static IEnumerable<string> All => typeof(ErrorCodes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>
    /// The code of a refusal the runtime's libraries raise: each refusal type carries the dictionary's code, set where it is
    /// thrown (Issue I-103). Nothing is read from a message's words.
    /// </summary>
    public static string Of(Exception e) => e switch
    {
        ServiceRefused r => r.Code,
        SignatureRejected s => s.Code,
        TransactHub.Api.PackageRejected p => p.Code,
        TransactHub.Store.ReleaseConflict r => r.Code,
        InsertOnlyViolation i => i.Code,
        TransactHub.Store.DistributionRefused d => d.Code,
        TransactHub.Security.SecretUnavailable => SecretUnavailable,
        HttpRequestException => SourceUnreachable,
        ActionRefused a => a.Code,
        _ => "Failure",
    };
}

public sealed record ErrorEntry(string Code, string Scope, string Dictionary, string Release, JsonObject Doc)
{
    public string Category => (string?)Doc["category"] ?? "technical";
    public bool Retryable => Doc["retryable"]?.GetValue<bool>() == true;
    public string? Title => (string?)Doc["title"];
    public IEnumerable<string> Actions => (Doc["runbook"]?["actions"] as JsonArray ?? []).Select(a => (string)a!);
    /// <summary>The console's link to the runbook: the web app's route for the entry.</summary>
    public string Link => RunbookLink(Code);
    public static string RunbookLink(string code) => $"#/service/errors/{Uri.EscapeDataString(code)}";
}

/// <summary>
/// The error dictionaries the runtime knows (spec R-05-51, R-05-52): those published on the content branch (the folder the
/// host is given, as the operations views are), and those that ship in every stored package under <c>errors/</c>. A code is
/// looked up in the asked scope first (a scheme), then its cluster platform, then <c>p</c>.
/// </summary>
public sealed class ErrorDictionary
{
    readonly Dictionary<string, Dictionary<string, ErrorEntry>> byScope = new(StringComparer.Ordinal);
    readonly List<string> sources = new();
    public IReadOnlyList<string> Sources => sources;

    public IEnumerable<ErrorEntry> All => byScope.Values.SelectMany(d => d.Values).OrderBy(e => e.Scope, StringComparer.Ordinal).ThenBy(e => e.Code, StringComparer.Ordinal);

    public static ErrorDictionary FromFolder(string? folder)
    {
        var d = new ErrorDictionary();
        if (folder is null || !Directory.Exists(folder)) return d;
        foreach (var f in Directory.EnumerateFiles(folder, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(f);
            if (!text.Contains("kind: error-dictionary")) continue;
            d.Add(text, Path.GetRelativePath(folder, f));
        }
        return d;
    }

    /// <summary>Adds a dictionary document. A later release of the same dictionary replaces the entries of an earlier one.</summary>
    public void Add(string yaml, string source)
    {
        if (Yaml.ToJson(yaml) is not JsonObject doc || (string?)doc["kind"] != "error-dictionary") return;
        var name = (string)doc["name"]!;
        var release = (string?)doc["release"] ?? "1.0.0";
        var parts = name.Split('.');
        var scope = parts[0] is "p" or "b" ? parts[0] : $"{parts[0]}.{parts[1]}";
        var map = byScope.TryGetValue(scope, out var m) ? m : byScope[scope] = new(StringComparer.Ordinal);
        foreach (var e in (doc["entries"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var code = (string)e["code"]!;
            if (map.TryGetValue(code, out var had) && Compare(had.Release, release) > 0) continue;
            map[code] = new ErrorEntry(code, scope, name, release, e);
        }
        sources.Add(source);
    }

    /// <summary>Adds the dictionaries a package carries (<c>errors/*.yaml</c>), from its canonical zip.</summary>
    public void AddPackage(byte[] zip, string source)
    {
        try
        {
            foreach (var (name, bytes) in SafeZip.Read(zip, x => x.StartsWith("errors/") && x.EndsWith(".yaml")))
                Add(Encoding.UTF8.GetString(bytes), $"{source}:{name}");
        }
        catch (InvalidDataException) { /* not a zip: nothing to add */ }
    }

    /// <summary>Adds the dictionaries of every stored release.</summary>
    public async Task AddStored(StoreOptions store, CancellationToken ct = default)
    {
        await using var db = StoreDbContext.Open(store);
        await foreach (var r in db.Releases.AsNoTracking().Select(r => new { r.Id, r.Package }).AsAsyncEnumerable().WithCancellation(ct)) AddPackage(r.Package, r.Id);
    }

    public static IEnumerable<string> ScopesFor(string? scope)
    {
        if (scope is null or "p") return ["p"];
        if (scope == "b") return ["b", "p"];
        if (scope.EndsWith(".p")) return [scope, "p"];
        return [scope, scope.Split('.')[0] + ".p", "p"];
    }

    /// <summary>The entry for a code, as a payment of <paramref name="scope"/> (its scheme) sees it; any scope when none is given.</summary>
    public ErrorEntry? Find(string code, string? scope = null)
    {
        foreach (var s in ScopesFor(scope))
            if (byScope.TryGetValue(s, out var m) && m.TryGetValue(code, out var e)) return e;
        return scope is null ? byScope.Values.Select(m => m.GetValueOrDefault(code)).FirstOrDefault(x => x is not null) : null;
    }

    /// <summary>Codes that appear in a text (an error message, a fact), longest first, so a log line can link each to its runbook.</summary>
    public IEnumerable<ErrorEntry> FoundIn(string? text, string? scope = null)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var seen = new HashSet<string>();
        foreach (var e in All.OrderByDescending(x => x.Code.Length))
            if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![A-Za-z0-9.:-]){System.Text.RegularExpressions.Regex.Escape(e.Code)}(?![A-Za-z0-9])") && seen.Add(e.Code))
                yield return Find(e.Code, scope) ?? e;
    }

    static int Compare(string a, string b)
    {
        int[] P(string r) => r.Split('.').Select(x => int.TryParse(x, out var i) ? i : 0).ToArray();
        var (x, y) = (P(a), P(b));
        for (var i = 0; i < 3; i++) { var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0); if (c != 0) return c; }
        return 0;
    }
}
