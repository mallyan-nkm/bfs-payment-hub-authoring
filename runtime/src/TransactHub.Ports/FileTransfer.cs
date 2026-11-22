using System.Collections.Concurrent;
using TransactHub.Security;

namespace TransactHub.Ports;

// The file-transfer port (p.port.file-transfer.v1, R-12-12) on the runtime: a file put in a folder of the bank's counterpart,
// read back, listed and removed. Which transport carries it is the bank's binding. This runtime builds two adapters: `simulator`,
// an in-memory folder tree for tests and the example bank, and `directory`, a folder on the host's file system (a mounted share,
// or a folder an SFTP gateway mirrors). An `sftp` binding is read but not built here (Issue I-129): the adapter that names it
// refuses, and nothing is sent.

/// <summary>A file in or out, by folder and name.</summary>
public interface IFileTransfer
{
    string Adapter { get; }
    Task Put(string folder, string name, byte[] content, CancellationToken ct = default);
    Task<byte[]?> Get(string folder, string name, CancellationToken ct = default);
    Task<bool> Delete(string folder, string name, CancellationToken ct = default);
    Task<IReadOnlyList<string>> List(string folder, CancellationToken ct = default);
}

public static class FileTransferAdapters
{
    public const string Port = "p.port.file-transfer.v1";

    public static IFileTransfer Create(PortBinding b, ISecretResolver? secrets = null) => b.Adapter switch
    {
        SimulatedFileTransfer.Name => new SimulatedFileTransfer(),
        DirectoryFileTransfer.Name => new DirectoryFileTransfer(b, secrets),
        _ => throw new PortBindingException($"{b.Port} for {b.Tenant} names adapter {b.Adapter}, which this runtime does not build (Issue I-129)"),
    };

    /// <summary>A folder and a name joined as a relative path; a name that climbs out of its folder is refused.</summary>
    public static string Relative(string folder, string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name is "." or "..")
            throw new PortBindingException($"'{name}' is not a file name");
        var parts = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == ".." || p == ".")) throw new PortBindingException($"'{folder}' is not a folder this port may use");
        return string.Join('/', parts.Append(name));
    }
}

/// <summary>An in-memory folder tree: what was put is there to read, list and remove. Shared by every caller of one adapter.</summary>
public sealed class SimulatedFileTransfer : IFileTransfer
{
    public const string Name = "simulator";
    public string Adapter => Name;
    readonly ConcurrentDictionary<string, byte[]> files = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Snapshot() => new Dictionary<string, byte[]>(files);

    public Task Put(string folder, string name, byte[] content, CancellationToken ct = default)
    {
        files[FileTransferAdapters.Relative(folder, name)] = content.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> Get(string folder, string name, CancellationToken ct = default) =>
        Task.FromResult(files.TryGetValue(FileTransferAdapters.Relative(folder, name), out var c) ? c.ToArray() : null);

    public Task<bool> Delete(string folder, string name, CancellationToken ct = default) =>
        Task.FromResult(files.TryRemove(FileTransferAdapters.Relative(folder, name), out _));

    public Task<IReadOnlyList<string>> List(string folder, CancellationToken ct = default)
    {
        var prefix = FileTransferAdapters.Relative(folder, "x")[..^1];
        return Task.FromResult<IReadOnlyList<string>>(files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k[prefix.Length..].Contains('/'))
            .Select(k => k[prefix.Length..]).Order(StringComparer.Ordinal).ToList());
    }
}

/// <summary>
/// A folder on the host. Setting <c>rootRef</c>: the root folder, by reference (a deployment's mount point is set where it runs,
/// as a secret is, so no host path is in the bank's configuration). A file is written to a temporary name and moved into place,
/// so a reader never sees half a file.
/// </summary>
public sealed class DirectoryFileTransfer(PortBinding b, ISecretResolver? secrets = null) : IFileTransfer
{
    public const string Name = "directory";
    public string Adapter => Name;
    readonly ISecretResolver secrets = secrets ?? SecretResolver.FromEnvironment();

    async Task<string> Root(CancellationToken ct)
    {
        var r = b.Required("rootRef");
        if (!SecretRef.IsRef(r)) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: rootRef must be a reference ({SecretRef.Forms})");
        var root = (await secrets.Resolve(r, ct)).Reveal();
        if (!Path.IsPathRooted(root)) throw new PortBindingException($"The {b.Adapter} binding of {b.Port} for {b.Tenant}: {r} is not an absolute folder");
        return root;
    }

    async Task<string> PathOf(string folder, string name, CancellationToken ct) => Path.Combine(await Root(ct), FileTransferAdapters.Relative(folder, name));

    public async Task Put(string folder, string name, byte[] content, CancellationToken ct = default)
    {
        var target = await PathOf(folder, name, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, target, overwrite: true);
    }

    public async Task<byte[]?> Get(string folder, string name, CancellationToken ct = default)
    {
        var p = await PathOf(folder, name, ct);
        return File.Exists(p) ? await File.ReadAllBytesAsync(p, ct) : null;
    }

    public async Task<bool> Delete(string folder, string name, CancellationToken ct = default)
    {
        var p = await PathOf(folder, name, ct);
        if (!File.Exists(p)) return false;
        File.Delete(p);
        return true;
    }

    public async Task<IReadOnlyList<string>> List(string folder, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(await PathOf(folder, "x", ct))!;
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().Where(n => !n.EndsWith(".part", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList()
            : [];
    }
}
