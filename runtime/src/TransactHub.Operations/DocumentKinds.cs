namespace TransactHub.Operations;

/// <summary>
/// The kinds an earlier runtime kept a document under, and the scheme message's id each is now (R-09-288, Issue I-363). The store is
/// insert-only, so a message or file kept before keeps its kind; the read model takes it as the message's id, so that a view's
/// <c>message:</c> source, a report and a gauge find one file under one kind, whenever it was kept.
/// </summary>
public static class DocumentKinds
{
    /// <summary>The former kind and the scheme message it is: the Central Bank SIF, kept by the store and sent by the File API.</summary>
    public static readonly IReadOnlyDictionary<string, string> Former = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["uae.wps.sif-out"] = "uae.wps.sif-submission",
    };

    /// <summary>The kind a document is read as: the scheme message's id for a former kind, otherwise the kind as kept.</summary>
    public static string Current(string kind) => Former.TryGetValue(kind, out var now) ? now : kind;
}
