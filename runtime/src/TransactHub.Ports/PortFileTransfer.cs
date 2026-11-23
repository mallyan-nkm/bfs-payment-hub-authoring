using TransactHub.Security;

namespace TransactHub.Ports;

// The `file-transfer` adapter of a port whose operations send and receive files (R-09-160, Issue I-192): the File API as a port that
// sends data out. A scheme's port (the WPS port, uae.wps.port.wps.v1) bound to it sends each file through the bank's binding of
// p.port.file-transfer.v1, which on a host of the runtime is the File API's outbox (R-09-84, R-09-138): the File API keeps the file,
// delivers it to the partner's folder on its own binding (SFTP or a folder), records the delivery, and resends it on an operator's
// action. What the partner answers comes back the same way: the File API's inbox of the partner names the message (I-160 (1)), and
// its front door correlates the file to the payment (I-160 (2)).
//
// Its settings, per send operation of the port:
//
//   <operation>.folder      where the file goes: the name of a setting of the bank's p.port.file-transfer.v1 binding, as
//                           `<partner>.<setting>` (centralBank.sifFolder), whose value is the partner's folder; the file is put in
//                           `<partner>/<that folder>`. A value with a slash is a folder of the file-transfer binding as it is.
//   <operation>.kind        what the File API keeps the file as (default: the port's operation, `<port>#<operation>`).
//
// and, informative for the reader of the binding, `<operation>.inbox` (the file-transfer binding's inbox the answer is read from).
// The adapter names no scheme: the bank's binding says where each file goes.

/// <summary>A file a port sends, and whose it is: the payment, its leg and the activity, so the File API keeps it with the payment.</summary>
public sealed record PortFile(string FileName, byte[] Content, string? PaymentId, string? Leg, string Scheme, string? Activity);

/// <summary>Where a port's file went: the file-transfer binding's adapter, the folder and the name.</summary>
public sealed record PortFileSent(string Adapter, string Folder, string FileName, string Kind);

public static class PortFileTransfer
{
    public const string Name = "file-transfer";

    /// <summary>
    /// Sends a port's file on the bank's binding of <paramref name="port"/> in force, which names adapter <c>file-transfer</c>: it is
    /// put on the bank's file-transfer binding in force, at the folder the port's binding names for <paramref name="operation"/>.
    /// </summary>
    public static async Task<PortFileSent> Send(PortBindings bindings, string tenant, string port, string operation, PortFile file, DateTimeOffset at,
        Func<PortBinding, IFileTransfer>? transferOf = null, ISecretResolver? secrets = null, CancellationToken ct = default)
    {
        var b = bindings.InForce(tenant, port, at);
        if (b.Adapter != Name) throw new PortBindingException($"{port} for {tenant} is bound to {b.Adapter}, not {Name}");
        var ft = bindings.InForce(tenant, FileTransferAdapters.Port, at);
        var folder = FolderOf(b, ft, operation);
        var kind = b.Setting($"{operation}.kind", $"{port}#{operation}");
        var transfer = (transferOf ?? (x => FileTransferAdapters.Create(x, secrets)))(ft);
        using (FileTransferOwner.Use(new FileTransferOwner(file.PaymentId, file.Leg, file.Scheme, kind, file.Activity)))
            await transfer.Put(folder, file.FileName, file.Content, ct);
        return new PortFileSent(ft.Adapter, folder, file.FileName, kind);
    }

    /// <summary>The folder of the file-transfer binding a port's operation sends to (<c>&lt;operation&gt;.folder</c>).</summary>
    public static string FolderOf(PortBinding port, PortBinding fileTransfer, string operation)
    {
        var named = port.Required($"{operation}.folder");
        if (named.Contains('/')) return named;
        var dot = named.IndexOf('.');
        if (dot <= 0 || dot == named.Length - 1) throw new PortBindingException($"The {Name} binding of {port.Port} for {port.Tenant}: {operation}.folder names '{named}', which is neither <partner>.<setting> of {FileTransferAdapters.Port} nor a folder");
        var partner = named[..dot];
        var value = fileTransfer.Settings.TryGetValue(named, out var v) && v.Length > 0 ? v
            : throw new PortBindingException($"The {Name} binding of {port.Port} for {port.Tenant} sends {operation} to {named}, which the bank's {FileTransferAdapters.Port} binding does not set");
        // A folder of an SFTP partner is named by the partner first (Sftp.cs); a folder binding is one tree, the partner a folder of it.
        return $"{partner}/{value.TrimStart('/')}";
    }
}
