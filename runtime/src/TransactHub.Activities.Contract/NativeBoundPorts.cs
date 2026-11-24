using System.Text;
using System.Text.Json.Nodes;

namespace TransactHub.Activities.Native;

// Bound ports, since native contract 2.0.0 (spec R-09-261, Issue I-315): what a module needs to build a bound adapter of its own, so a
// scheme's adapters live in its module and the image answers no scheme's port. A module reads the binding in force for an attempt (its
// adapter and settings) and sends a request to the binding's endpoint; the image keeps the endpoint, the credential, the circuit
// breaker and the timeout. A module never sees a secret, never names a host, and never sends on a binding that is not in force.

/// <summary>
/// One port's binding in force for an attempt: the bank's choice of adapter, from a moment on, with its settings as strings. A setting
/// that names a credential (<c>secretRef</c>) is a reference only; the image resolves it when it sends.
/// </summary>
public sealed record NativeBinding(string Tenant, string Port, string Adapter, DateTimeOffset ValidFrom, string? Release, IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>A setting, or <paramref name="fallback"/> when the binding has none.</summary>
    public string Setting(string key, string fallback = "") => Settings.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>A setting the adapter needs; a binding without it is refused (<see cref="NativePortRefused"/>), never defaulted.</summary>
    public string Required(string key) => Settings.TryGetValue(key, out var v) && v.Length > 0 ? v
        : throw new NativePortRefused(Port, $"The {Adapter} binding of {Port} for {Tenant} has no '{key}'");

    /// <summary>A comma list setting, trimmed, without empty entries.</summary>
    public IReadOnlySet<string> List(string key) =>
        Setting(key).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// A request a module sends on a binding: its method, a path below the binding's <c>baseUrl</c> (never a host of its own), an optional
/// query, a JSON body, and headers of its own. The credential's header (<c>Authorization</c>, or the binding's <c>credentialHeader</c>)
/// is the image's: a module may not set it. Since 2.1.0 (R-09-280) the body may instead be bytes with their content type
/// (<see cref="Content"/>): a file, a signed XML envelope, a form. A request carries one body or none, never both.
/// </summary>
public sealed record NativeHttpRequest(string Method, string Path, JsonNode? Body = null, IReadOnlyDictionary<string, string>? Headers = null, string? Query = null)
{
    /// <summary>Since 2.1.0. A body of bytes, sent as they are with their content type, in place of <see cref="Body"/>.</summary>
    public NativeContent? Content { get; init; }
}

/// <summary>Since 2.1.0. A body of bytes and its content type (<c>application/xml; charset=utf-8</c>, <c>text/plain</c>, ...).</summary>
public sealed record NativeContent(string ContentType, byte[] Bytes);

/// <summary>
/// Since 2.1.0 (R-09-281). A file a module puts through a binding's transfer: the port's operation it is for (the binding's
/// <c>&lt;operation&gt;.folder</c> names where it goes, and <c>&lt;operation&gt;.kind</c> its kind), its name and its bytes, and the payment
/// and leg it belongs to.
/// </summary>
public sealed record NativeFilePut(string Operation, string FileName, byte[] Content, string? PaymentId = null, string? Leg = null)
{
    /// <summary>
    /// Since 2.2.0 (R-09-293, Issue I-385). The scheme message the file is (the message whose <c>disposition.sentBy</c> names the send,
    /// R-09-234): the File API keeps the file as that message, in the message's folder where the binding names one
    /// (<c>&lt;operation&gt;.&lt;message&gt;.folder</c>, R-09-235). None: the binding's <c>&lt;operation&gt;.kind</c> and <c>&lt;operation&gt;.folder</c>.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>Since 2.1.0. A file put: the transfer's adapter that took it (<c>file-api</c>, <c>sftp</c>, ...), the folder, its name, kind and size.</summary>
public sealed record NativeFileSent(string Via, string Folder, string FileName, string Kind, int Bytes);

/// <summary>What the endpoint answered: its status, its content type and its body.</summary>
public sealed record NativeHttpResponse(int Status, string? ContentType, byte[] Body)
{
    public bool Success => Status is >= 200 and < 300;
    public string Text() => Encoding.UTF8.GetString(Body);
    public JsonObject? Json() => Body.Length == 0 ? null : JsonNode.Parse(Body) as JsonObject;
}

/// <summary>
/// The bank's bindings, as a module reads and calls them (R-09-261).
/// <list type="bullet">
/// <item><see cref="For"/>: the binding of <c>port</c> in force for the attempt's tenant at its time; <c>NoTenant</c> or <c>PortNotBound</c> otherwise.</item>
/// <item><see cref="Send"/>: the request sent to the binding's <c>baseUrl</c>, with the binding's credential (<c>secretRef</c>, as a
/// bearer token or in <c>credentialHeader</c>), behind the endpoint's circuit breaker, within the binding's <c>timeoutSeconds</c>
/// (10 by default). The image sends only on the binding in force for the attempt, with its own copy of the settings: a binding that is
/// not the one in force is refused (<c>PortNotBound</c>). A party that does not answer, a timeout, a 5xx or a 429 is a retryable
/// <c>TransientError</c>; an open breaker the retryable <c>port.CircuitOpen</c>; a credential that cannot be read <c>SecretUnavailable</c>;
/// a binding without its <c>baseUrl</c>, a path that names a host or a header the image keeps is a <see cref="NativePortRefused"/>.
/// Any other status is the module's to read. Since 2.1.0 a request with a body of bytes (<see cref="NativeHttpRequest.Content"/>) sends
/// them as they are, under their content type; one that names a content type that is not one, or carries both bodies, is a
/// <see cref="NativePortRefused"/>.</item>
/// <item><see cref="Put"/>, since 2.1.0 (R-09-281): the file put through the binding's transfer. The binding in force names adapter
/// <c>file-transfer</c>; the file goes on the bank's binding of <c>p.port.file-transfer.v1</c> in force at the same moment (the File
/// API's outbox on a runtime, or an SFTP partner), at the folder the port's binding names for the operation, with the payment and leg
/// as its owner. A binding that is not the one in force, or names another adapter, is refused (<c>PortNotBound</c>); a binding without the
/// operation's folder a <see cref="NativePortRefused"/>; a transfer that did not take the file a retryable <c>TransientError</c>, an open
/// breaker the retryable <c>port.CircuitOpen</c>.</item>
/// </list>
/// </summary>
public interface INativeBoundPorts
{
    NativeBinding For(NativeAttempt a, string port);
    NativeHttpResponse Send(NativeAttempt a, NativeBinding binding, NativeHttpRequest request);

    /// <summary>Since 2.1.0. A file put through the binding's transfer (R-09-281).</summary>
    NativeFileSent Put(NativeAttempt a, NativeBinding binding, NativeFilePut file);
}

/// <summary>
/// A module's gate on a port's binding (R-09-264): called by the image before each call it makes on the port for an attempt, with the
/// binding in force. It returns when the call may go, and throws a <see cref="NativeFailure"/> when it may not (a retryable
/// <c>TransientError</c> while a window is closed, <c>BusinessReject</c> for a binding whose gate settings are invalid). A module
/// contributes it under <see cref="NativeContributions.BindingGate"/>.
/// </summary>
public delegate void NativeBindingGate(NativeAttempt a, NativeBinding binding);
