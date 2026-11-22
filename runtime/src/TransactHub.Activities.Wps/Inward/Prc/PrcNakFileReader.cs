// Ported from the TransactHub source (666f1ee): services/activities/platform/PaymentHub.Activities.Platform.Wps/Inward/PrcNakFileReader.cs.
// Behaviour unchanged; only the namespace moved (plan P2). The source's own unit tests run against it (TransactHub.Activities.Wps.Tests).
// Purpose: The pure reader of a staged inward PRC NAK file (design Inward_PAF_PRC_Nak_Extraction v1.0 §5 steps 5-6, §6.3; task T1).
// Responsibility: re-verify the staged bytes (length, SHA-256), parse them with WpsAckNakFile, check the AHR against the PRC the step is applying, and
// extract EVERY DER, trimmed, with its description (D-A10-03): the file-level code is the last DER's, a 3-field DER is read leniently, warnings never stop
// the step (D-A10-04). No I/O, no clock, no database: the staged bytes arrive as an argument so the rules are unit-testable.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TransactHub.Activities.Wps.Rules;

namespace TransactHub.Activities.Wps.Inward;

/// <summary>What was read from a staged NAK file: the parsed document, every rejection, the file-level code and the warnings (design §6.3).</summary>
/// <param name="Document">The parsed document, with the raw text verbatim (<see cref="WpsAckNakDocument.RawContent"/>, I-N5).</param>
/// <param name="Rejections">Every DER, in file order, trimmed (D-A10-03).</param>
/// <param name="RejectionCode">The last DER's error number; empty when there is no DER (E-N6).</param>
/// <param name="Warnings">Warning codes; none of them stops the step (D-A10-04).</param>
/// <param name="AhrStatus">The AHR status (at most 10 characters), or null when the file has no AHR (E-N4).</param>
public sealed record PrcNakExtract(
    WpsAckNakDocument Document,
    IReadOnlyList<WpsAckDetailError> Rejections,
    string RejectionCode,
    IReadOnlyList<string> Warnings,
    string? AhrStatus)
{
    /// <summary>The <c>FileContent</c> of the NAK record: <c>{document, rejections, rejectionCode, warnings}</c> (design §6.3).</summary>
    public string ToFileContentJson() =>
        JsonSerializer.Serialize(
            new
            {
                document = Document,
                rejections = Rejections,
                rejectionCode = RejectionCode,
                warnings = Warnings,
            },
            WpsAckNakJson.Options);
}

/// <summary>The result of <see cref="PrcNakFileReader.Read"/>: an extract, or the reason the file cannot be applied.</summary>
public sealed record PrcNakReadResult(PrcNakExtract? Extract, string? Failure)
{
    /// <summary>True when <see cref="Extract"/> is set.</summary>
    public bool IsValid => Extract is not null;
}

/// <summary>The warning codes of <see cref="PrcNakExtract.Warnings"/>.</summary>
public static class PrcNakWarnings
{
    public const string NoAhr = "NO_AHR";
    public const string AhrStatusUnexpected = "AHR_STATUS_UNEXPECTED";
    public const string NoDerRecord = "NO_DER_RECORD";
    public const string DerWithoutDescription = "DER_WITHOUT_DESCRIPTION";
    public const string DerMalformed = "DER_MALFORMED";
    public const string DerLineNumberInvalid = "DER_LINE_NUMBER_INVALID";
    public const string NoAtr = "NO_ATR";
    public const string AtrCountMismatch = "ATR_COUNT_MISMATCH";
    public const string AhrAtrStatusMismatch = "AHR_ATR_STATUS_MISMATCH";
}

/// <summary>Reads a staged PRC NAK file (design §5 steps 5-6).</summary>
public static class PrcNakFileReader
{
    private const int AhrStatusMaxLength = 10;

    /// <summary>
    /// Verifies and reads <paramref name="bytes"/>. A failure (length, hash, AHR ACCEPTED, AHR names another PRC) returns
    /// <see cref="PrcNakReadResult.Failure"/>; everything else is a warning (E-N3..E-N8).
    /// </summary>
    /// <param name="bytes">The staged file's bytes.</param>
    /// <param name="sizeBytes">The size the watcher recorded.</param>
    /// <param name="sha256">The SHA-256 the watcher recorded (hex, any case).</param>
    /// <param name="prcFileName">The PRC this NAK answers; a present AHR must name it (ignoring case).</param>
    /// <param name="sourceFileName">The NAK's remote file name, used for the WPS file id split.</param>
    public static PrcNakReadResult Read(byte[] bytes, long sizeBytes, string sha256, string prcFileName, string? sourceFileName)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(prcFileName);

        if (bytes.LongLength != sizeBytes)
        {
            return Fail($"The staged NAK file is {bytes.LongLength} bytes; the watcher recorded {sizeBytes}.");
        }

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), sha256, StringComparison.OrdinalIgnoreCase))
        {
            return Fail("The staged NAK file's SHA-256 differs from the one the watcher recorded.");
        }

        var document = WpsAckNakFile.Parse(new UTF8Encoding(false).GetString(bytes).TrimStart('﻿'), sourceFileName);
        var warnings = new List<string>();

        if (document.Status is null)
        {
            warnings.Add(PrcNakWarnings.NoAhr);
        }
        else
        {
            if (document.IsAccepted)
            {
                return Fail("The NAK file's AHR status is ACCEPTED, so it is not a NAK.");
            }

            if (!string.Equals(document.ProcessedFileName, prcFileName, StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"The NAK file's AHR names '{document.ProcessedFileName ?? "none"}', not '{prcFileName}'.");
            }

            if (!string.Equals(document.Status, WpsAckNakFile.StatusRejected, StringComparison.Ordinal))
            {
                warnings.Add(PrcNakWarnings.AhrStatusUnexpected);
            }
        }

        var rejections = ReadRejections(document.RawContent, warnings);
        if (rejections.Count == 0)
        {
            warnings.Add(PrcNakWarnings.NoDerRecord);
        }

        AddTrailerWarnings(document, warnings);

        var ahrStatus = document.Status is null
            ? null
            : (document.Status.Length <= AhrStatusMaxLength ? document.Status : document.Status[..AhrStatusMaxLength]);
        var code = rejections.Count == 0 ? string.Empty : rejections[^1].ErrorNumber;
        return new PrcNakReadResult(new PrcNakExtract(document, rejections, code, warnings, ahrStatus), null);
    }

    private static PrcNakReadResult Fail(string reason) => new(null, reason);

    // Every DER from the raw text, so that a 3-field DER (no description) is still read; WpsAckNakFile.Parse drops it as malformed (E-N7).
    private static List<WpsAckDetailError> ReadRejections(string rawContent, List<string> warnings)
    {
        var rejections = new List<WpsAckDetailError>();
        var lines = rawContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l));

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var parts = trimmed.Split(',', 4);
            if (!string.Equals(parts[0].Trim(), WpsAckNakFile.RecordTypeDetailError, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parts.Length < 3)
            {
                AddOnce(warnings, PrcNakWarnings.DerMalformed);
                continue;
            }

            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber))
            {
                AddOnce(warnings, PrcNakWarnings.DerLineNumberInvalid);
            }

            var description = string.Empty;
            if (parts.Length >= 4)
            {
                description = parts[3].Trim();
            }
            else
            {
                AddOnce(warnings, PrcNakWarnings.DerWithoutDescription);
            }

            rejections.Add(new WpsAckDetailError(lineNumber, parts[2].Trim(), description));
        }

        return rejections;
    }

    private static void AddTrailerWarnings(WpsAckNakDocument document, List<string> warnings)
    {
        if (document.TrailerStatus is null)
        {
            warnings.Add(PrcNakWarnings.NoAtr);
            return;
        }

        if (document.TrailerLineCount is { } declared && declared != document.ActualLineCount)
        {
            warnings.Add(PrcNakWarnings.AtrCountMismatch);
        }

        if (document.Status is not null && !string.Equals(document.Status, document.TrailerStatus, StringComparison.Ordinal))
        {
            warnings.Add(PrcNakWarnings.AhrAtrStatusMismatch);
        }
    }

    private static void AddOnce(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning))
        {
            warnings.Add(warning);
        }
    }
}
