using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Ports;

// The `file-bulk` adapter of the core-banking port (owner's decision of 2026-11-22, Issue I-82, R-12-15): a bank that posts by
// file instead of by call. A batch of postings becomes one bulk file in the layout of the source's DE generation (WPS inward
// Activity 12, DeBulkFileBuilder, layout FSD-PROVISIONAL-2); the file goes to core banking's landing folder through the bank's
// file-transfer port (Activity 13); the upload is submitted (Activity 14) and its status read until core banking answers
// (Activity 15, getDeuploadStatus); the lines it failed are posted again in a file of their own (Activity 16). It is another
// connection, not a scheme step: a bank chooses it in its binding of p.port.core-banking.v1, and no workflow changes.
//
// Idempotent by batch: the file's name is the batch's reference, so the same batch is the same file. A batch answered once is
// answered again from memory; after a restart, core banking's "File Already Uploaded" is read as the upload having been taken,
// and its status is read instead of submitting again.

/// <summary>How one line of a bulk file came out.</summary>
public sealed record PostingLineResult(int Line, string Account, bool Posted, string Code, int Generation);

/// <summary>A bulk file as built: its name, bytes and hash.</summary>
public sealed record BulkFile(string Name, byte[] Content, string Sha256, int Generation, IReadOnlyList<int> Lines);

/// <summary>
/// The source's DE bulk file layout (DeBulkFileBuilder, FSD-PROVISIONAL-2), from a posting batch: ASCII, '~'-separated, CRLF after
/// each line. A header (branch, source code, description, batch reference), then one line per leg: its line number (the reference
/// core banking answers it by), branch, account, currency, value date twice, amount, local amount, D or C, rate, transaction code,
/// narration, and the three columns the source filled from the salary file (blank here), with an extra '~' on a credit line.
/// </summary>
public static class BulkFileLayout
{
    public const string Layout1 = "FSD-PROVISIONAL-1";
    public const string Layout2 = "FSD-PROVISIONAL-2";
    const string Crlf = "\r\n";
    const char Sep = '~';
    const string LocalRate = "1.00000";

    /// <summary>'~', CR and LF become a space; a value with a character outside printable ASCII is written blank (the source's DeFieldSanitiser).</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '~' or '\r' or '\n') chars[i] = ' ';
            else if (chars[i] < ' ' || chars[i] > '~') return "";
        }
        return new string(chars);
    }

    static string Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>The file's text for the legs at <paramref name="lines"/> (1-based) of the batch.</summary>
    public static byte[] Build(PostingBatch batch, IReadOnlyList<int> lines, string batchReference, string headerBranch, string sourceCode, string description, string layout)
    {
        if (layout is not (Layout1 or Layout2)) throw new PortBindingException($"No bulk file layout is called '{layout}'");
        var date = batch.ValueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append(string.Join(Sep, Field(headerBranch), Field(sourceCode), Field(description), Field(batchReference))).Append(Crlf);
        foreach (var n in lines)
        {
            var l = batch.Legs[n - 1];
            var debit = l.Kind == "debit";
            var amount = Money(l.Amount);
            var cols = new[] { n.ToString(CultureInfo.InvariantCulture), Field(l.Branch), Field(l.Account), Field(l.Currency), date, date, amount, amount, debit ? "D" : "C", LocalRate, Field(l.TxnCode), Field(l.Narration), "", "", "" };
            text.Append(string.Join(Sep, cols));
            if (!debit) text.Append(Sep);
            text.Append(Crlf);
        }
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>The legs of a file this layout wrote: line number, account and D or C. For the simulator and tests.</summary>
    public static IReadOnlyList<(int Line, string Account, string Side)> Read(byte[] file) =>
        Encoding.ASCII.GetString(file).Split(Crlf, StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(r => r.Split(Sep)).Select(c => (int.Parse(c[0], CultureInfo.InvariantCulture), c[2], c[8])).ToList();
}

public enum UploadOutcome { Accepted, AlreadyUploaded, Rejected }
public sealed record UploadReply(UploadOutcome Outcome, string? Code = null, string? Message = null);

/// <summary>What a status query answered (the source's DeOutcomeReplyReader classes).</summary>
public enum UploadStatusKind { Records, NoRecord, FileNotFound, Fault, Empty }
/// <summary>One record of a status reply: the line it names (external reference) and core banking's code for it.</summary>
public sealed record UploadStatusRecord(string? ExternalRef, string? ErrorCode, string? ErrorMessage);
public sealed record UploadStatus(UploadStatusKind Kind, IReadOnlyList<UploadStatusRecord> Records, string? FaultCode = null);

/// <summary>Core banking's side of a bulk upload: submit a file it can read from its landing folder, and say how its lines went.</summary>
public interface IBulkUploadGateway
{
    Task<UploadReply> Submit(string folder, string fileName, string idempotencyKey, CancellationToken ct = default);
    Task<UploadStatus> Status(string fileName, CancellationToken ct = default);
}

/// <summary>A posting whose fate core banking has not told: it may have posted. Read by the activities as <c>unknown</c>, never posted again blind.</summary>
public sealed class PostingFateUnknown(string message) : IOException(message);

/// <summary>The <c>file-bulk</c> adapter. Settings are listed on <see cref="FileBulkCoreBanking.SettingNames"/>.</summary>
public sealed class FileBulkCoreBanking(PortBinding b, IFileTransfer transfer, IBulkUploadGateway gateway, ICoreBanking balance,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : ICoreBanking
{
    public const string Name = "file-bulk";
    public string Adapter => Name;

    /// <summary>
    /// The binding's settings: <c>folder</c> (core banking's landing folder on the file-transfer port), <c>headerBranch</c>,
    /// <c>sourceCode</c>, <c>batchDescription</c> (the file's header), <c>layout</c> (FSD-PROVISIONAL-2 by default),
    /// <c>fileNamePrefix</c>, <c>localCurrency</c> (the one currency a file carries, AED by default), <c>statusPolls</c> and
    /// <c>statusPollSeconds</c> (how often and how far apart the status is read), <c>repostAttempts</c> and <c>repostCodes</c>
    /// (the failed lines posted again, and the codes that are; every code when empty), and <c>upload</c> (<c>gateway-rest</c>
    /// or <c>simulator</c>) with that gateway's own settings.
    /// </summary>
    public static readonly IReadOnlyList<string> SettingNames =
        ["folder", "headerBranch", "sourceCode", "batchDescription", "layout", "fileNamePrefix", "localCurrency", "statusPolls", "statusPollSeconds", "repostAttempts", "repostCodes", "upload"];

    readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;
    readonly ConcurrentDictionary<string, PostingAnswer> answered = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);
    readonly List<BulkFile> sent = new();

    /// <summary>The files this adapter has put on the file-transfer port, in order.</summary>
    public IReadOnlyList<BulkFile> Sent { get { lock (sent) return sent.ToList(); } }

    public Task<BalanceAnswer> Balance(string branch, string account, CancellationToken ct = default) => balance.Balance(branch, account, ct);

    /// <summary>
    /// The fate of a posting by its batch reference (R-08-19): the answer this adapter gave, else the status of the batch's first file:
    /// no failed line is posted, a failed line or a fault is not posted, and a file core banking has not seen is not known.
    /// </summary>
    public async Task<PostingInquiry> Inquire(string reference, CancellationToken ct = default)
    {
        if (answered.TryGetValue(reference, out var a)) return new PostingInquiry(reference, true, a.Posted, a.HostReference);
        var name = FileNameOf(b.Setting("fileNamePrefix", "DE"), reference, 1);
        var s = await gateway.Status(name, ct);
        static bool Clean(UploadStatusRecord r) => (r.ErrorCode?.Trim() ?? "") is "" or "0" or "12";
        return s.Kind switch
        {
            UploadStatusKind.NoRecord => new PostingInquiry(reference, true, true, name),
            UploadStatusKind.Records when s.Records.All(Clean) => new PostingInquiry(reference, true, true, name),
            UploadStatusKind.Records or UploadStatusKind.Fault => new PostingInquiry(reference, true, false, null),
            _ => new PostingInquiry(reference, false, false, null),
        };
    }

    int Int(string key, int fallback, int min) => int.TryParse(b.Setting(key, ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min ? n : fallback;

    public static string FileNameOf(string prefix, string reference, int generation)
    {
        var safe = new string(reference.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        return generation == 1 ? $"{prefix}{safe}.txt" : $"{prefix}{safe}-R{generation - 1}.txt";
    }

    public async Task<PostingAnswer> Post(PostingBatch batch, CancellationToken ct = default)
    {
        if (answered.TryGetValue(batch.Reference, out var done)) return done;
        var gate = locks.GetOrAdd(batch.Reference, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (answered.TryGetValue(batch.Reference, out done)) return done;
            var answer = await PostOnce(batch, ct);
            return answered.GetOrAdd(batch.Reference, answer);
        }
        finally { gate.Release(); }
    }

    async Task<PostingAnswer> PostOnce(PostingBatch batch, CancellationToken ct)
    {
        var folder = b.Required("folder");
        var local = b.Setting("localCurrency", "AED");
        if (batch.Legs.Count == 0) return new PostingAnswer(batch.Reference, false, "NO-LEGS", null);
        // A bulk file carries one currency and the rate 1: a leg in another is refused before anything is sent.
        var foreign = batch.Legs.Select((l, i) => (l, n: i + 1)).Where(x => !string.Equals(x.l.Currency, local, StringComparison.OrdinalIgnoreCase)).ToList();
        if (foreign.Count > 0)
            return new PostingAnswer(batch.Reference, false, "CCY", null, foreign.Select(x => new PostingLineResult(x.n, x.l.Account, false, "CCY", 0)).ToList());
        var repostAttempts = Int("repostAttempts", 1, 0);
        var repostCodes = b.Setting("repostCodes", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new Dictionary<int, PostingLineResult>();
        IReadOnlyList<int> pending = Enumerable.Range(1, batch.Legs.Count).ToList();
        string? firstFile = null;
        for (var generation = 1; pending.Count > 0 && generation <= repostAttempts + 1; generation++)
        {
            var file = Build(batch, pending, generation);
            firstFile ??= file.Name;
            await Transfer(folder, file, ct);
            var reply = await gateway.Submit(folder, file.Name, $"{batch.Reference}:g{generation}", ct);
            if (reply.Outcome == UploadOutcome.Rejected)
            {
                // Core banking refused the file as a whole: none of its lines posted.
                foreach (var n in pending) results[n] = new(n, batch.Legs[n - 1].Account, false, reply.Code ?? "REJECTED", generation);
                break;
            }
            var failed = await Outcome(file, ct);
            foreach (var n in pending)
                results[n] = failed.TryGetValue(n, out var code) ? new(n, batch.Legs[n - 1].Account, false, code, generation) : new(n, batch.Legs[n - 1].Account, true, "0", generation);
            // The lines core banking failed, with a code the bank reposts, go again in a file of their own.
            pending = failed.Where(f => repostCodes.Count == 0 || repostCodes.Contains(f.Value)).Select(f => f.Key).Order().ToList();
        }
        var lines = results.Values.OrderBy(r => r.Line).ToList();
        var posted = lines.All(l => l.Posted);
        return new PostingAnswer(batch.Reference, posted, posted ? "0" : lines.First(l => !l.Posted).Code, posted ? firstFile : null, lines);
    }

    BulkFile Build(PostingBatch batch, IReadOnlyList<int> lines, int generation)
    {
        var reference = generation == 1 ? batch.Reference : $"{batch.Reference}-R{generation - 1}";
        var content = BulkFileLayout.Build(batch, lines, reference, b.Required("headerBranch"), b.Required("sourceCode"), b.Required("batchDescription"),
            b.Setting("layout", BulkFileLayout.Layout2));
        return new BulkFile(FileNameOf(b.Setting("fileNamePrefix", "DE"), batch.Reference, generation), content, Convert.ToHexStringLower(SHA256.HashData(content)), generation, lines);
    }

    /// <summary>The source's transfer (Activity 13): remove a file of that name, put the file, read it back and check it is the same.</summary>
    async Task Transfer(string folder, BulkFile file, CancellationToken ct)
    {
        await transfer.Delete(folder, file.Name, ct);
        await transfer.Put(folder, file.Name, file.Content, ct);
        var back = await transfer.Get(folder, file.Name, ct);
        if (back is null || !back.AsSpan().SequenceEqual(file.Content))
            throw new IOException($"The bulk file {file.Name} did not read back as it was put on {FileTransferAdapters.Port}");
        lock (sent) sent.Add(file);
    }

    /// <summary>
    /// The upload's outcome (Activity 15): the status is read until core banking answers it, at most <c>statusPolls</c> times. Records
    /// name the lines that failed, by their line number; a line with no record, or a clean code (0, 12), posted. "No record" (12) is
    /// every line posted. Still no answer after the last read: the fate is unknown.
    /// </summary>
    async Task<Dictionary<int, string>> Outcome(BulkFile file, CancellationToken ct)
    {
        var polls = Int("statusPolls", 8, 1);
        var wait = TimeSpan.FromSeconds(Int("statusPollSeconds", 30, 0));
        for (var i = 1; i <= polls; i++)
        {
            var s = await gateway.Status(file.Name, ct);
            switch (s.Kind)
            {
                case UploadStatusKind.NoRecord:
                    return new();
                case UploadStatusKind.Records:
                    var failed = new Dictionary<int, string>();
                    foreach (var r in s.Records)
                    {
                        var code = r.ErrorCode?.Trim() ?? "";
                        if (code is "" or "0" or "12") continue;
                        if (int.TryParse(r.ExternalRef?.Replace("~", "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && file.Lines.Contains(n)) failed[n] = code;
                    }
                    return failed;
                case UploadStatusKind.Fault:
                    return file.Lines.ToDictionary(n => n, _ => s.FaultCode ?? "FAULT");
            }
            if (i < polls) await delay(wait, ct);
        }
        throw new PostingFateUnknown($"Core banking gave no outcome for {file.Name} after {polls} status reads");
    }
}

/// <summary>
/// The upload calls over the same REST gateway as <see cref="GatewayRestCoreBanking"/>: settings <c>baseUrl</c>, <c>uploadPath</c>,
/// <c>statusPath</c>, <c>uploadFunctionId</c>, <c>statusFunctionId</c>, <c>uploadUser</c>, <c>uploadBranch</c>, <c>externalHostId</c>,
/// <c>successCode</c> and the credential by <c>secretRef</c>. The reply's shape is the source's (the T24 upload's code and message;
/// getDeuploadStatus's records and fault).
/// </summary>
public sealed class RestBulkUploadGateway(PortBinding b, HttpClient http, ISecretResolver? secrets = null) : IBulkUploadGateway
{
    public const string Name = "gateway-rest";
    public const string AlreadyUploaded = "File Already Uploaded";
    readonly ISecretResolver secrets = secrets ?? SecretResolver.FromEnvironment();

    async Task<HttpRequestMessage> Request(string path, string function, string reference, JsonObject body, CancellationToken ct)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(b.Required("baseUrl")), b.Required(path))) { Content = JsonContent.Create(body) };
        m.Headers.Add("X-Function-Id", b.Required(function));
        m.Headers.Add("X-External-Host-Id", b.Required("externalHostId"));
        m.Headers.Add("X-Ref", reference);
        if (b.Setting("secretRef", "") is { Length: > 0 } r)
            m.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await secrets.Resolve(r, ct)).Reveal());
        return m;
    }

    public async Task<UploadReply> Submit(string folder, string fileName, string idempotencyKey, CancellationToken ct = default)
    {
        var body = new JsonObject { ["functionId"] = b.Required("uploadFunctionId"), ["userId"] = b.Required("uploadUser"), ["branch"] = b.Required("uploadBranch"), ["fileName"] = fileName };
        using var res = await http.SendAsync(await Request("uploadPath", "uploadFunctionId", idempotencyKey, body, ct), ct);
        res.EnsureSuccessStatusCode();
        var j = (await res.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        var code = (string?)j["code"] ?? "";
        var message = (string?)j["message"] ?? "";
        if (code == b.Setting("successCode", "0")) return new(UploadOutcome.Accepted, code, message);
        return message.Contains(AlreadyUploaded, StringComparison.OrdinalIgnoreCase) ? new(UploadOutcome.AlreadyUploaded, code, message) : new(UploadOutcome.Rejected, code, message);
    }

    public async Task<UploadStatus> Status(string fileName, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(await Request("statusPath", "statusFunctionId", fileName, new JsonObject { ["fileName"] = fileName }, ct), ct);
        res.EnsureSuccessStatusCode();
        return Read(await res.Content.ReadFromJsonAsync<JsonObject>(ct));
    }

    /// <summary>The source's reader: records, a single object or a list's objects; else a fault with a code (12 no record, 901 file not found).</summary>
    public static UploadStatus Read(JsonObject? j)
    {
        static IEnumerable<JsonObject> OneOrMany(JsonNode? n) => n switch { JsonArray a => a.OfType<JsonObject>(), JsonObject o => [o], _ => [] };
        static string? T(JsonObject o, string k) => o[k] is JsonValue v ? v.ToString() : null;
        if (j is null) return new(UploadStatusKind.Empty, []);
        var records = OneOrMany(j["records"]).Select(r => new UploadStatusRecord(T(r, "EXTERNAL_REF_NO"), T(r, "ErrorCode"), T(r, "ErrorMessage"))).ToList();
        if (records.Count > 0) return new(UploadStatusKind.Records, records);
        if (OneOrMany(j["fault"]).FirstOrDefault() is { } f && T(f, "ErrorCode")?.Trim() is { Length: > 0 } code)
            return new(code == "12" ? UploadStatusKind.NoRecord : code == "901" ? UploadStatusKind.FileNotFound : UploadStatusKind.Fault, [], code);
        return new(UploadStatusKind.Empty, []);
    }
}

/// <summary>
/// A core banking that takes bulk files, for tests and the example bank: it reads the file from the landing folder and posts every
/// line, except a line on an account in <c>simulator.failAccounts</c>, which it fails with <c>simulator.failCode</c> the first
/// <c>simulator.failTimes</c> times it sees it. It answers <c>simulator.pendingPolls</c> status reads with "file not found" first,
/// and a file name it has taken before with "File Already Uploaded".
/// </summary>
public sealed class SimulatedBulkUploadGateway(PortBinding b, IFileTransfer transfer) : IBulkUploadGateway
{
    public const string Name = "simulator";
    readonly ConcurrentDictionary<string, (string Folder, int Polls, List<UploadStatusRecord> Failed)> uploads = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, int> failures = new(StringComparer.Ordinal);
    public int Submitted;

    public async Task<UploadReply> Submit(string folder, string fileName, string idempotencyKey, CancellationToken ct = default)
    {
        if (uploads.ContainsKey(fileName)) return new(UploadOutcome.AlreadyUploaded, "1", RestBulkUploadGateway.AlreadyUploaded);
        var content = await transfer.Get(folder, fileName, ct);
        if (content is null) return new(UploadOutcome.Rejected, "1", "invalid file operation");
        Interlocked.Increment(ref Submitted);
        var fail = b.Setting("simulator.failAccounts", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var times = int.TryParse(b.Setting("simulator.failTimes", "1"), out var t) ? t : 1;
        var failed = new List<UploadStatusRecord>();
        foreach (var (line, account, _) in BulkFileLayout.Read(content))
            if (fail.Contains(account) && failures.AddOrUpdate(account, 1, (_, n) => n + 1) <= times)
                failed.Add(new(line.ToString(CultureInfo.InvariantCulture), b.Setting("simulator.failCode", "AC04"), "Simulated failure"));
        uploads[fileName] = (folder, 0, failed);
        return new(UploadOutcome.Accepted, "0", "Uploaded");
    }

    public Task<UploadStatus> Status(string fileName, CancellationToken ct = default)
    {
        if (!uploads.TryGetValue(fileName, out var u)) return Task.FromResult(new UploadStatus(UploadStatusKind.FileNotFound, [], "901"));
        var pending = int.TryParse(b.Setting("simulator.pendingPolls", "0"), out var p) ? p : 0;
        uploads[fileName] = u with { Polls = u.Polls + 1 };
        if (u.Polls < pending) return Task.FromResult(new UploadStatus(UploadStatusKind.FileNotFound, [], "901"));
        return Task.FromResult(u.Failed.Count == 0 ? new UploadStatus(UploadStatusKind.NoRecord, [], "12") : new UploadStatus(UploadStatusKind.Records, u.Failed));
    }
}
