using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TransactHub.Ports;

namespace TransactHub.FileApi;

// The inboxes the File API polls (R-09-82, R-09-86): a folder of a bank's partner on its binding of p.port.file-transfer.v1, read
// through the same adapter that delivers the outbox. On a laptop the binding is `directory` and the inbox is a folder; at a bank it is
// `sftp` and the inbox is a folder of the Central Bank, the labour ministry or core banking's file interface. A file younger than the
// inbox's minimum age is left for the next poll (it may still be arriving); a file taken is moved to the inbox's done folder when the
// intake accepted it, else to its error folder, so a file is read once.
//
// An inbox is named in the binding, by partner: `<partner>.folder` (with `<partner>.message`, `<partner>.doneFolder`,
// `<partner>.errorFolder`, `<partner>.minimumAgeSeconds`), or several as `<partner>.inbox.<name>.folder` and the same keys under it. The
// installation may add inboxes in its settings (`FileApi:Inboxes:<n>:Tenant|Folder|Message|DoneFolder|ErrorFolder|MinimumAgeSeconds`).

/// <summary>One inbox: whose, where on the binding, which message its files are, where taken files go, and how old a file must be.</summary>
/// <param name="StemDrop">How many characters the partner adds to the end of the stem of the file a received one answers (<c>stemDrop</c>,
/// I-160 (2), R-09-162): the received file's stem without them is the key it is correlated by, before its own stem.</param>
public sealed record Inbox(string Tenant, string Folder, string Message, string? DoneFolder, string? ErrorFolder, TimeSpan MinimumAge, int StemDrop = 0)
{
    /// <summary>The correlation keys of a received file the inbox's binding names: the stem of the file it answers (R-09-162).</summary>
    public IReadOnlyDictionary<string, string>? KeysOf(string fileName)
    {
        if (StemDrop <= 0) return null;
        var stem = RuntimeFrontDoor.Stem(fileName);
        return stem.Length > StemDrop ? new Dictionary<string, string>(StringComparer.Ordinal) { ["fileStem"] = stem[..^StemDrop] } : null;
    }

    /// <summary>The inboxes a binding names: every partner with a folder and the message its files are.</summary>
    public static IEnumerable<Inbox> Of(PortBinding b)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (k, v) in b.Settings)
        {
            var parts = k.Split('.');
            string? group = null, key = null;
            if (parts.Length == 2 && parts[1] is "folder" or "message" or "doneFolder" or "errorFolder" or "minimumAgeSeconds" or "stemDrop") (group, key) = (parts[0], parts[1]);
            else if (parts.Length == 4 && parts[1] == "inbox") (group, key) = ($"{parts[0]}.inbox.{parts[2]}", parts[3]);
            if (group is null) continue;
            if (!groups.TryGetValue(group, out var g)) groups[group] = g = new(StringComparer.Ordinal);
            g[key!] = v;
        }
        foreach (var (group, g) in groups.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            // A folder whose files are no message the bank names is not an inbox the File API reads (Issue I-160).
            if (!g.TryGetValue("folder", out var folder) || folder.Length == 0 || !g.TryGetValue("message", out var message) || message.Length == 0) continue;
            var partner = group.Split('.')[0];
            string At(string f) => b.Adapter == SftpFileTransfer.Name ? $"{partner}/{f.TrimStart('/')}" : f;
            string? Rel(string? f) => f is { Length: > 0 } ? f.Contains('/') ? At(f) : $"{At(folder).TrimEnd('/')}/{f}" : null;
            var age = g.TryGetValue("minimumAgeSeconds", out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? TimeSpan.FromSeconds(n) : TimeSpan.Zero;
            var drop = g.TryGetValue("stemDrop", out var sd) && int.TryParse(sd, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dn) && dn > 0 ? dn : 0;
            yield return new Inbox(b.Tenant, At(folder), message, Rel(g.GetValueOrDefault("doneFolder")), Rel(g.GetValueOrDefault("errorFolder")), age, drop);
        }
    }

    public static IEnumerable<Inbox> Of(IConfiguration cfg) =>
        cfg.GetSection("FileApi:Inboxes").GetChildren().Select(s => new Inbox(
            s["Tenant"] ?? throw new InvalidOperationException($"FileApi:Inboxes:{s.Key} names no Tenant"),
            s["Folder"] ?? throw new InvalidOperationException($"FileApi:Inboxes:{s.Key} names no Folder"),
            s["Message"] ?? throw new InvalidOperationException($"FileApi:Inboxes:{s.Key} names no Message"),
            s["DoneFolder"], s["ErrorFolder"], TimeSpan.FromSeconds(int.TryParse(s["MinimumAgeSeconds"], out var n) ? n : 0)));
}

/// <summary>
/// Polls the inboxes once: each file old enough is taken, given to the intake, and moved out of the inbox. One runtime serves one
/// bank (R-09-76): with <paramref name="bank"/> (<c>TH_BANK</c>) an inbox of another bank is never read, whatever the bindings or
/// the installation's settings name (R-09-104), so a file of another bank is neither taken nor moved.
/// </summary>
public sealed class InboxPoller(ITransferPorts ports, FileIntake intake, string environment, Func<IEnumerable<Inbox>> inboxes, TimeProvider? clock = null, ILogger? log = null, string? bank = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    readonly HashSet<string> told = new(StringComparer.Ordinal);

    /// <summary>The inboxes this runtime reads: those of its bank only, when it names one. Another bank's is named in the log once.</summary>
    public IEnumerable<Inbox> Inboxes() => inboxes().Where(box =>
    {
        if (bank is not { Length: > 0 } || box.Tenant == bank) return true;
        lock (told)
            if (told.Add($"{box.Tenant}|{box.Folder}")) log?.LogWarning("Inbox {Folder} of {Tenant} is not polled: this runtime serves {Bank} only", box.Folder, box.Tenant, bank);
        return false;
    });

    public async Task<List<Received>> PollOnce(CancellationToken ct = default)
    {
        var taken = new List<Received>();
        foreach (var box in Inboxes())
        {
            IFileTransfer t;
            try { t = ports.For(box.Tenant, clock.GetUtcNow()).Transfer; }
            catch (PortBindingException e) { log?.LogWarning("Inbox {Folder} of {Tenant} is not polled: {Why}", box.Folder, box.Tenant, e.Message); continue; }
            IReadOnlyList<TransferFile> files;
            try { files = t is IFileTransferListing l ? await l.ListFiles(box.Folder, ct) : (await t.List(box.Folder, ct)).Select(n => new TransferFile(n, -1, null)).ToList(); }
            catch (Exception e) when (e is FileTransferException or IOException or PortBindingException or TransactHub.Security.SecretUnavailable)
            {
                log?.LogWarning("Inbox {Folder} of {Tenant} could not be read: {Why}", box.Folder, box.Tenant, e.Message);
                continue;
            }
            foreach (var f in files)
            {
                if (f.Modified is { } m && clock.GetUtcNow() - m < box.MinimumAge) continue;
                var content = await t.Get(box.Folder, f.Name, ct);
                if (content is null) continue;
                var r = await intake.Receive(new Arrival(box.Tenant, environment, box.Message, f.Name, content, "inbox", $"inbox:{box.Folder}", box.KeysOf(f.Name)), ct);
                taken.Add(r);
                var to = r.Accepted ? box.DoneFolder : box.ErrorFolder ?? box.DoneFolder;
                // Out of the inbox, so it is read once: to its done or error folder when the inbox names one, else removed (it is kept in the store).
                if (to is not null) await t.Put(to, f.Name, content, ct);
                await t.Delete(box.Folder, f.Name, ct);
            }
        }
        return taken;
    }
}

/// <summary>The File API's background work: poll the inboxes and deliver the outbox, every <c>FileApi:PollSeconds</c> (default 10).</summary>
public sealed class FileApiWorker(InboxPoller poller, FileOutbox outbox, IConfiguration cfg, ILogger<FileApiWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromSeconds(int.TryParse(cfg["FileApi:PollSeconds"], out var s) && s > 0 ? s : 10);
        var attempts = int.TryParse(cfg["FileApi:DeliveryAttempts"], out var a) && a > 0 ? a : 5;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await poller.PollOnce(stoppingToken);
                foreach (var id in await outbox.Pending(attempts, stoppingToken)) await outbox.Deliver(id, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "The File API's poll failed"); }
            try { await Task.Delay(every, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
