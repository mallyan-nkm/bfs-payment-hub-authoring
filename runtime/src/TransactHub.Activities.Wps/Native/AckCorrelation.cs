using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Rules;

namespace TransactHub.Activities.Wps.Native;

/// <summary>
/// <c>uae.wps.map.ack-correlation.v1</c> (Issue I-267, I-160 (2), R-09-212): the correlation map of the acknowledgement's deliver
/// disposition. The front door runs it on an ACK or NAK it receives, before the file is kept, and matches the payment by the key it
/// gives: the stem of the salary file the answer names (<c>fileStem</c>), as the sent file was recorded. The source's reading
/// (<see cref="WpsAckNakFile"/>): the AHR record's processed file name when the file carries one, else the answer's own name split from
/// the back before the Central Bank's file id. Provisional, as the scheme is.
/// </summary>
public sealed class MapAckCorrelation : INativeActivity
{
    public string Name => "uae.wps.map.ack-correlation.v1";

    static string Stem(string fileName)
    {
        var n = Path.GetFileName(fileName);
        var i = n.IndexOf('.');
        return i > 0 ? n[..i] : n;
    }

    public NativeResult Run(NativeAttempt a)
    {
        var inbound = NativeContext.Node(a.View, "messages.inbound");
        var payload = NativeContext.S(inbound, "payload");
        var fileName = NativeContext.S(inbound, "fileName");
        string? key = null;
        if (payload.TrimStart().StartsWith(WpsAckNakFile.RecordTypeHeader + ",", StringComparison.OrdinalIgnoreCase)
            && WpsAckNakFile.Parse(payload, fileName.Length > 0 ? fileName : null).ProcessedFileName is { Length: > 0 } processed)
            key = Stem(processed);
        if (key is null && WpsAckNakFile.TrySplitFileName(fileName, out var prefix, out _)) key = Stem(prefix);
        if (string.IsNullOrEmpty(key)) return new NativeResult("uncorrelated", "FF01", new JsonObject());
        return new NativeResult("correlated", null, new JsonObject { ["messages.correlation"] = new JsonObject { ["fileStem"] = key } });
    }
}
