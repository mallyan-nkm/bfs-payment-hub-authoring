using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransactHub.Activities.Wps.Rules;

/// <summary>
/// Reader/writer for the CBUAE WPS acknowledgement file (ACK / NAK), per UAEWPS SSM V2020-001 5.2
/// (WPS File ID), 5.3.5/5.3.6 (naming) and 5.6.46-5.6.48 (AHR / DER / ATR records).
/// </summary>
/// <remarks>
/// Lives in Shared because three separate processes need the same understanding of this format and
/// must not drift: PaymentHub.Worker.CbFileWatcher parses what the Central Bank sends, the WPS
/// activity persists the parsed result, and PaymentHub.Simulator.API generates stand-in files that
/// have to be byte-shaped like the real thing or the stub proves nothing.
/// <para>
/// The single most important rule here is the one that makes correlation possible at all: the
/// employer id is <b>variable length (13-35)</b>, so an acknowledgement file name can only be split
/// from the <b>back</b>. The WPS File ID is fixed at exactly 12 characters (NYYFFFFFFFFF), therefore
/// the original SIF prefix is everything except the final 12. Splitting from the front - or assuming
/// the acknowledgement is named identically to the SIF - silently produces a name that matches no
/// transaction. Verified against two real CBUAE files (a 37-character ACK and NAK, each resolving to
/// a 25-character SIF prefix plus a 12-character file id).
/// </para>
/// </remarks>
public static class WpsAckNakFile
{
    /// <summary>SSM 5.2: the WPS File ID (NYYFFFFFFFFF) is always exactly this many characters.</summary>
    public const int WpsFileIdLength = 12;

    public const string RecordTypeHeader = "AHR";
    public const string RecordTypeDetailError = "DER";
    public const string RecordTypeTrailer = "ATR";

    public const string StatusAccepted = "ACCEPTED";
    public const string StatusRejected = "REJECTED";

    /// <summary>
    /// Splits an acknowledgement file name into the original SIF prefix and the WPS File ID.
    /// </summary>
    /// <param name="ackFileName">
    /// With or without the .ACK/.NAK extension, with or without directories.
    /// </param>
    /// <returns>
    /// False when the name is too short to carry a 12-character file id, which means it does not
    /// follow the SSM convention and must not be guessed at.
    /// </returns>
    public static bool TrySplitFileName(string ackFileName, out string sifPrefix, out string wpsFileId)
    {
        sifPrefix = string.Empty;
        wpsFileId = string.Empty;

        if (string.IsNullOrWhiteSpace(ackFileName))
            return false;

        var baseName = ackFileName;

        var lastSlash = baseName.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0)
            baseName = baseName[(lastSlash + 1)..];

        var lastDot = baseName.LastIndexOf('.');
        if (lastDot > 0)
            baseName = baseName[..lastDot];

        // Strictly greater than: a name that is only a file id has no SIF prefix at all, which is
        // not a valid acknowledgement name.
        if (baseName.Length <= WpsFileIdLength)
            return false;

        sifPrefix = baseName[..^WpsFileIdLength];
        wpsFileId = baseName[^WpsFileIdLength..];
        return true;
    }

    /// <summary>
    /// The SIF file name an acknowledgement refers to, derived from the acknowledgement's own name.
    /// This is the fallback path; <see cref="WpsAckNakDocument.ProcessedFileName"/> read from the AHR
    /// record is authoritative and should be preferred when the file could be parsed.
    /// </summary>
    public static bool TryDeriveSifFileName(string ackFileName, out string sifFileName)
    {
        sifFileName = string.Empty;
        if (!TrySplitFileName(ackFileName, out var prefix, out _))
            return false;

        sifFileName = prefix + ".SIF";
        return true;
    }

    /// <summary>Builds the acknowledgement file name the CB would produce for a given SIF.</summary>
    public static string BuildFileName(string sifFileName, string wpsFileId, bool accepted)
    {
        var prefix = sifFileName.EndsWith(".SIF", StringComparison.OrdinalIgnoreCase)
            ? sifFileName[..^4]
            : sifFileName;

        return $"{prefix}{wpsFileId}.{(accepted ? "ACK" : "NAK")}";
    }

    /// <summary>
    /// Generates a WPS File ID in the SSM 5.2 shape: N (file type) + YY (year) + 9-digit sequence.
    /// Only for the simulator - the real value always comes from the Central Bank.
    /// </summary>
    public static string GenerateWpsFileId(char fileTypeIdentifier, DateTime utcNow, int sequence) =>
        $"{fileTypeIdentifier}{utcNow:yy}{(sequence % 1_000_000_000):D9}";

    /// <summary>
    /// Parses acknowledgement file text. Never throws on malformed input: an unparseable file must
    /// be quarantined with its raw content preserved, not crash the poller.
    /// </summary>
    public static WpsAckNakDocument Parse(string content, string? sourceFileName = null)
    {
        var errors = new List<string>();
        var details = new List<WpsAckDetailError>();
        string? status = null;
        string? processedFileName = null;
        string? trailerStatus = null;
        int? trailerLineCount = null;

        var lines = (content ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var recordType = trimmed.Split(',', 2)[0].Trim().ToUpperInvariant();

            switch (recordType)
            {
                case RecordTypeHeader:
                {
                    // AHR,<ACCEPTED|REJECTED>,<processed file name>. Limit of 3 so a file name that
                    // somehow contained a comma survives intact rather than being truncated.
                    var parts = trimmed.Split(',', 3);
                    if (parts.Length < 3)
                    {
                        errors.Add($"AHR record is malformed (expected 3 fields, found {parts.Length}): '{trimmed}'");
                        break;
                    }

                    status = parts[1].Trim().ToUpperInvariant();
                    processedFileName = parts[2].Trim();
                    break;
                }

                case RecordTypeDetailError:
                {
                    // DER,<line number>,<error number>,<description>. The description is free text of
                    // up to 200 characters and is taken as the remainder, so an embedded comma is
                    // preserved instead of splitting the record.
                    var parts = trimmed.Split(',', 4);
                    if (parts.Length < 4)
                    {
                        errors.Add($"DER record is malformed (expected 4 fields, found {parts.Length}): '{trimmed}'");
                        break;
                    }

                    details.Add(new WpsAckDetailError(
                        int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber) ? lineNumber : 0,
                        parts[2].Trim(),
                        parts[3].Trim()));
                    break;
                }

                case RecordTypeTrailer:
                {
                    var parts = trimmed.Split(',', 3);
                    if (parts.Length < 3)
                    {
                        errors.Add($"ATR record is malformed (expected 3 fields, found {parts.Length}): '{trimmed}'");
                        break;
                    }

                    trailerStatus = parts[1].Trim().ToUpperInvariant();
                    trailerLineCount = int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : null;
                    break;
                }

                default:
                    errors.Add($"Unrecognised record type '{recordType}': '{trimmed}'");
                    break;
            }
        }

        if (status is null)
            errors.Add("No AHR record was present, so the acknowledgement status and processed file name are unknown.");

        if (trailerStatus is null)
            errors.Add("No ATR record was present, so the file may be truncated.");

        // SSM 5.6.48: the trailer count includes the trailer record itself.
        if (trailerLineCount is { } declared && declared != lines.Count)
            errors.Add($"ATR declares {declared} line(s) but the file contains {lines.Count} - the file may be truncated or padded.");

        if (status is not null && trailerStatus is not null && !string.Equals(status, trailerStatus, StringComparison.Ordinal))
            errors.Add($"AHR status '{status}' and ATR status '{trailerStatus}' disagree.");

        if (string.Equals(status, StatusAccepted, StringComparison.Ordinal) && details.Count > 0)
            errors.Add($"AHR says ACCEPTED but {details.Count} DER record(s) are present; DER is valid only when REJECTED (SSM 5.6.47).");

        string? wpsFileId = null;
        string? derivedSifFileName = null;
        if (sourceFileName is not null && TrySplitFileName(sourceFileName, out var prefix, out var id))
        {
            wpsFileId = id;
            derivedSifFileName = prefix + ".SIF";
        }

        return new WpsAckNakDocument(
            Status: status,
            ProcessedFileName: processedFileName,
            DetailErrors: details,
            TrailerStatus: trailerStatus,
            TrailerLineCount: trailerLineCount,
            ActualLineCount: lines.Count,
            SourceFileName: sourceFileName is null ? null : SftpFileName(sourceFileName),
            WpsFileId: wpsFileId,
            DerivedSifFileName: derivedSifFileName,
            ParseErrors: errors,
            RawContent: content ?? string.Empty);
    }

    /// <summary>Renders an acknowledgement file exactly as the SSM defines it.</summary>
    public static string Build(string processedSifFileName, bool accepted, IReadOnlyList<WpsAckDetailError>? detailErrors = null)
    {
        var status = accepted ? StatusAccepted : StatusRejected;
        var builder = new StringBuilder();

        builder.Append(RecordTypeHeader).Append(',').Append(status).Append(',').Append(processedSifFileName).Append('\n');

        var lineCount = 1;
        if (!accepted && detailErrors is not null)
        {
            foreach (var detail in detailErrors)
            {
                builder.Append(RecordTypeDetailError).Append(',')
                       .Append(detail.LineNumber.ToString(CultureInfo.InvariantCulture)).Append(',')
                       .Append(detail.ErrorNumber).Append(',')
                       .Append(detail.ErrorDescription).Append('\n');
                lineCount++;
            }
        }

        // SSM 5.6.48: "the number of lines in the file including this trailer record".
        lineCount++;
        builder.Append(RecordTypeTrailer).Append(',').Append(status).Append(',')
               .Append(lineCount.ToString(CultureInfo.InvariantCulture)).Append('\n');

        return builder.ToString();
    }

    private static string SftpFileName(string path)
    {
        var lastSlash = path.LastIndexOfAny(['/', '\\']);
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}

/// <summary>One DER record - SSM 5.6.47.</summary>
public sealed record WpsAckDetailError(
    [property: JsonPropertyName("lineNumber")] int LineNumber,
    [property: JsonPropertyName("errorNumber")] string ErrorNumber,
    [property: JsonPropertyName("errorDescription")] string ErrorDescription);

/// <summary>
/// A parsed acknowledgement file. Serialised verbatim into
/// <c>wps.CbAcknowledgements.FileContent</c>, so the shape here is the stored contract: parsed
/// records for querying, plus the untouched original text so the exact bytes the Central Bank sent
/// remain available for dispute and audit.
/// </summary>
public sealed record WpsAckNakDocument(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("processedFileName")] string? ProcessedFileName,
    [property: JsonPropertyName("detailErrors")] IReadOnlyList<WpsAckDetailError> DetailErrors,
    [property: JsonPropertyName("trailerStatus")] string? TrailerStatus,
    [property: JsonPropertyName("trailerLineCount")] int? TrailerLineCount,
    [property: JsonPropertyName("actualLineCount")] int ActualLineCount,
    [property: JsonPropertyName("sourceFileName")] string? SourceFileName,
    [property: JsonPropertyName("wpsFileId")] string? WpsFileId,
    [property: JsonPropertyName("derivedSifFileName")] string? DerivedSifFileName,
    [property: JsonPropertyName("parseErrors")] IReadOnlyList<string> ParseErrors,
    [property: JsonPropertyName("rawContent")] string RawContent)
{
    /// <summary>True when the CB accepted the file (AHR field 2 is ACCEPTED).</summary>
    [JsonIgnore]
    public bool IsAccepted => string.Equals(Status, WpsAckNakFile.StatusAccepted, StringComparison.Ordinal);

    /// <summary>True when nothing about the file contradicted the SSM.</summary>
    [JsonIgnore]
    public bool IsWellFormed => ParseErrors.Count == 0;

    /// <summary>
    /// The SIF file name to correlate on. The AHR record is authoritative (SSM 5.6.46 field 3 is the
    /// processed file name itself); the name-derived value is only used when the record is missing
    /// or unreadable.
    /// </summary>
    [JsonIgnore]
    public string? CorrelationFileName =>
        string.IsNullOrWhiteSpace(ProcessedFileName) ? DerivedSifFileName : ProcessedFileName;

    /// <summary>
    /// True when the AHR record and the file name point at different SIF files. That is never
    /// expected, and means one of the two is wrong - the file must be quarantined rather than
    /// correlated on a guess.
    /// </summary>
    [JsonIgnore]
    public bool HasCorrelationConflict =>
        !string.IsNullOrWhiteSpace(ProcessedFileName)
        && !string.IsNullOrWhiteSpace(DerivedSifFileName)
        && !string.Equals(ProcessedFileName, DerivedSifFileName, StringComparison.OrdinalIgnoreCase);

    public string ToJson() => JsonSerializer.Serialize(this, WpsAckNakJson.Options);
}

public static class WpsAckNakJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
