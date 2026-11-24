using System.Text.Json.Nodes;

namespace TransactHub.Activities.Native;

// What the runtime image answers for a module, since native contract 1.1.0 (spec R-09-125, Issue I-220). A module references this
// assembly and no other of TransactHub: the posting through core banking, the bank's other ports, the host's files and a scheme's
// match of a registered party are reached through these types, and the image's adapters, bindings and store stay behind them. Since
// 2.0.0 a module also builds the adapters of its own ports on the bindings in force (NativeBoundPorts.cs, R-09-261).

/// <summary>One leg of a posting: debit or credit, the branch and account core banking knows, the amount, the transaction code and the narration.</summary>
public sealed record NativePostingLeg(string Kind, string Branch, string Account, decimal Amount, string Currency, string TxnCode, string Narration);

/// <summary>A posting: its reference (the idempotency key core banking sees), its value date and its legs.</summary>
public sealed record NativePostingBatch(string Reference, DateOnly ValueDate, IReadOnlyList<NativePostingLeg> Legs);

/// <summary>
/// What a posting came to: <paramref name="Outcome"/> is the primary outcome the activity named, <c>rejected</c> (core banking refused
/// it) or <c>unknown</c> (the call may have reached core banking; it is never posted again blindly); <paramref name="Fact"/> is the
/// posting as the context keeps it (the legs, the adapter, the binding's date, the reference, core banking's code and reference).
/// </summary>
public sealed record NativePosted(string Outcome, string? ReasonCode, JsonObject Fact);

/// <summary>The bank's core banking (<c>p.port.core-banking.v1</c>), as the image's adapter of the binding in force answers it.</summary>
public interface INativeCoreBanking
{
    /// <summary>
    /// The binding in force for the attempt's tenant at its time. It refuses an attempt with no tenant (<c>NoTenant</c>) and a tenant with
    /// no binding (<c>PortNotBound</c>). Since 2.0.0 the image then asks the gate a module of the distribution contributed on the binding
    /// (<see cref="NativeContributions.BindingGate"/> of <c>p.port.core-banking.v1</c>: the platform module's posting window, R-09-264), which
    /// fails as a retryable <c>TransientError</c> while the window is closed. Since 2.1.0 (R-09-279) the image names no setting of a window:
    /// it calls core banking only through a gate a module of the distribution contributed, and with none contributed every call is
    /// refused (<c>PortNotBound</c>), never made ungated.
    /// </summary>
    INativeCoreBankingBinding For(NativeAttempt a);
}

/// <summary>
/// Since 2.1.0. What core banking said of an account (R-09-277): the amount available, its currency and the account's status, as the
/// binding's adapter reads them. The status is core banking's own word (<c>ACTIVE</c>, <c>DORMANT</c>, ...); the binding says which it posts to.
/// </summary>
public sealed record NativeBalance(decimal Available, string Currency, string Status);

/// <summary>One core-banking binding, for one attempt.</summary>
public interface INativeCoreBankingBinding
{
    /// <summary>The adapter that answers (<c>simulator</c>, <c>rest</c>, <c>file-bulk</c>, ...).</summary>
    string Adapter { get; }

    /// <summary>Since 2.0.0. The binding itself: its adapter, its moment and its settings.</summary>
    NativeBinding Binding { get; }

    /// <summary>The account core banking knows for an account of the context: its id, else its IBAN as the binding keys it.</summary>
    string AccountOf(JsonNode? account);

    /// <summary>The branch of an account, by the binding's branch rule.</summary>
    string BranchFor(string account);

    /// <summary>Posts the batch and classifies the reply: <paramref name="primary"/> when posted, else <c>rejected</c> or <c>unknown</c>.</summary>
    NativePosted Post(NativePostingBatch batch, string primary);

    /// <summary>
    /// Since 2.1.0 (R-09-277, Issue I-382). A balance enquiry on an account at a branch, by the binding's adapter; it posts nothing. A party
    /// that does not answer is a retryable <c>TransientError</c>, a credential that cannot be read <c>SecretUnavailable</c>, a binding the
    /// adapter cannot use <c>BusinessReject</c>.
    /// </summary>
    NativeBalance Balance(string branch, string account);
}

/// <summary>An answer of a port: the adapter that gave it and what it said.</summary>
public sealed record NativePortAnswer(string Adapter, JsonObject Body);

/// <summary>The adapter of a bound port refused the request itself (a key it does not hold, a file it cannot read): not an outage.</summary>
public sealed class NativePortRefused(string port, string message) : Exception(message)
{
    public string Port { get; } = port;
}

/// <summary>
/// The ports the bank binds, other than core banking, each answered by the image's adapter of the binding in force for the attempt's
/// tenant at its time. The image says which ports and operations it answers; for each the request and the answer are JSON:
/// <list type="bullet">
/// <item><c>p.port.party-register.v1</c> <c>lookup</c>: <c>{ kind, field, value }</c> to <c>{ found, key, record }</c>;</item>
/// <item><c>p.port.encryption.v1</c> <c>encrypt</c>: <c>{ sha256, recipientKey, fileName }</c> (a file the host keeps) to <c>{ sha256, bytes }</c>, the encrypted file kept;</item>
/// <item><c>p.port.decryption.v1</c> <c>decrypt</c>: <c>{ sha256 }</c> (a file the host keeps) to <c>{ sha256, bytes }</c>, the decrypted file kept;</item>
/// <item>any port the bank binds to <c>file-transfer</c>: <c>{ sha256, fileName, paymentId, leg, scheme }</c> to <c>{ sent, via, folder, fileName, kind, bytes }</c>, the kept file sent.</item>
/// </list>
/// Since 2.0.0 the image answers no scheme's port: a scheme's module builds its own adapter on <see cref="INativeServices.BoundPorts"/>
/// (the labour ministry's register is the WPS module's, R-09-263).
/// A failure is a <see cref="NativeFailure"/>: <c>NoTenant</c>, <c>PortNotBound</c> (also for a port or operation the image does not
/// answer), <c>SecretUnavailable</c>, or a retryable <c>TransientError</c> when the party did not answer; a request the adapter itself
/// refused is a <see cref="NativePortRefused"/>.
/// </summary>
public interface INativePorts
{
    NativePortAnswer Ask(NativeAttempt a, string port, string operation, JsonObject request);
}

/// <summary>
/// The files a native host keeps (Issue I-97): what an activity renders, encrypts, receives or decrypts, by SHA-256. The host says where
/// (its memory, and the store when it has one) and whose (the attempt's tenant, payment and leg).
/// </summary>
public interface INativeFiles
{
    /// <summary>Keeps a file; <paramref name="direction"/> is "in" (received) or "out" (sent). Returns its SHA-256.</summary>
    string Keep(string direction, string kind, string fileName, byte[] content);
    /// <summary>A file kept earlier, by its SHA-256; null when the host holds none.</summary>
    byte[]? Get(string sha256);
}

/// <summary>A party the bank's register holds, in force at the step: its key and its record.</summary>
public sealed record NativeParty(string Key, JsonObject Record);

/// <summary>What a scheme's matcher says of a registered party against what the payment supplied: nothing wrong, or why not.</summary>
public sealed record PartyMatch(string Outcome, string ReasonCode, string SchemeCode, string Message);

/// <summary>
/// A scheme's own match of the party it found (R-12-27): for WPS, the source's SifUploadRules.MatchCorporate. Given the id asked,
/// the record in force (null when none is) and the attempt's view; null when the party is registered for the scheme. A scheme's
/// module contributes it under <c>party-matcher:&lt;scheme&gt;</c>.
/// </summary>
public delegate PartyMatch? PartyMatcher(string id, NativeParty? party, JsonObject view);
