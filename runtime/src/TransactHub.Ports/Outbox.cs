using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Ports;

/// <summary>
/// Whose a file is that an activity puts on the file-transfer port: the payment, the leg, the scheme and what the file is. The File
/// API keeps an outbox file under them, so it is shown in the payment's documents tab (R-09-64, R-09-84). Set around the put.
/// </summary>
public sealed record FileTransferOwner(string? PaymentId, string? Leg, string Scheme, string Kind, string? Activity)
{
    static readonly AsyncLocal<FileTransferOwner?> current = new();
    public static FileTransferOwner? Current => current.Value;

    public static IDisposable Use(FileTransferOwner owner)
    {
        var before = current.Value;
        current.Value = owner;
        return new Restore(() => current.Value = before);
    }

    sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>
/// The file-transfer port on a host whose files go through the File API (R-09-84): a put is a write to the File API's outbox
/// (<c>POST /outbox</c>), which keeps the file and delivers it on the bank's binding; a file the File API could not deliver yet stays in
/// its outbox and is tried again there. Reads, lists and removals are the binding's adapter's.
/// </summary>
public sealed class OutboxFileTransfer(IFileTransfer inner, string tenant, Uri fileApi, string tokenRef, ISecretResolver secrets, HttpClient? http = null) : IFileTransfer
{
    static readonly HttpClient shared = new() { Timeout = TimeSpan.FromMinutes(2) };
    readonly HttpClient http = http ?? shared;
    public string Adapter => inner.Adapter;
    public IFileTransfer Inner => inner;

    public async Task Put(string folder, string name, byte[] content, CancellationToken ct = default)
    {
        FileTransferAdapters.Relative(folder, name);
        var owner = FileTransferOwner.Current;
        var body = new JsonObject
        {
            ["tenant"] = tenant, ["scheme"] = owner?.Scheme ?? "p", ["kind"] = owner?.Kind ?? "file", ["folder"] = folder, ["fileName"] = name,
            ["contentBase64"] = Convert.ToBase64String(content), ["paymentId"] = owner?.PaymentId, ["leg"] = owner?.Leg, ["activity"] = owner?.Activity,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(fileApi, "outbox")) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await secrets.Resolve(tokenRef, ct)).Reveal());
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new FileTransferException($"The File API's outbox answered {(int)res.StatusCode} for {name}");
    }

    public Task<byte[]?> Get(string folder, string name, CancellationToken ct = default) => inner.Get(folder, name, ct);
    public Task<bool> Delete(string folder, string name, CancellationToken ct = default) => inner.Delete(folder, name, ct);
    public Task<IReadOnlyList<string>> List(string folder, CancellationToken ct = default) => inner.List(folder, ct);
}
