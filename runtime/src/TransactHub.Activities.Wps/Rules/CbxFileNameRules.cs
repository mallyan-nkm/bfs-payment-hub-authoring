// Purpose: The pure file-name rules a CBX salary file must pass (ported from the source's CbxFileNameRules, US 5001).
// Responsibility: rules 1–3 (plain name, extension, exact length, MOL id / timestamp split) and rules 5–7
// (year window, exact timestamp, not in the future), with the legacy CBX rejection messages. Used by both the
// CBX intake watcher and the upload API preflight, so the two cannot drift apart. Every bank-specific value
// comes from CbxRuleSettings; the 12-character timestamp and the ".SIF" / ".SIF.GPG" suffixes are the SIF
// naming format and the same for every bank. Rule 4/4a (MOL registration) and rule 8 (duplicates) need the
// database and stay with the API preflight.
namespace TransactHub.Activities.Wps.Rules;

using System.Globalization;

/// <summary>The parts of a CBX file name that passed rules 1–3.</summary>
/// <param name="MolEstablishmentId">The leading <see cref="CbxRuleSettings.MolIdLength"/> characters.</param>
/// <param name="DateComponent">The 12-character <c>yyMMddHHmmss</c> timestamp after the MOL id.</param>
/// <param name="IsEncrypted">True for a <c>.SIF.GPG</c> name, false for a plain <c>.SIF</c> name.</param>
/// <param name="SifFileName">The name without <c>.GPG</c>: the decrypted file's name, <c>{MOL}{yyMMddHHmmss}.SIF</c>.</param>
public sealed record CbxFileName(string MolEstablishmentId, string DateComponent, bool IsEncrypted, string SifFileName);

/// <summary>Pure CBX file-name rules. Error codes match <see cref="SifUploadRules"/>; messages are the legacy CBX texts.</summary>
public static class CbxFileNameRules
{
    public const string ExtensionMessage = "File Name extension does not contain .GPG or .SIF";
    public const string NotPlainNameMessage = "File Name is not a plain file name";
    public const string DateRangeMessage = "Date Time Format in filename is Incorrect";
    public const string DateFormatMessage = "Date Time format in filename is incorrect";
    public const string FutureDateMessage = "Date Time specified in filename should not be a future date";
    public const string MolIdInvalidMessage = "MOL Establishment Id in filename is invalid or does not match.";
    /// <summary>Rule 4 (applied by the upload API's corporate lookup); the legacy spelling is kept on purpose.</summary>
    public const string MolIdNotRegisteredMessage = "MOL Establishment Id in filename is not registred in WPS system.";

    private const int DateComponentLength = 12;
    private const int MaxFileNameLength = 255;
    private const string SifSuffix = SifFiles.SifSuffix;
    private const string GpgSuffix = SifFiles.GpgSuffix;

    /// <summary>The exact name length rule 2 expects: MOL id + 12-character timestamp + suffix.</summary>
    public static int ExpectedLength(CbxRuleSettings settings, bool encrypted)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MolIdLength + DateComponentLength + (encrypted ? SifSuffix.Length + GpgSuffix.Length : SifSuffix.Length);
    }

    /// <summary>
    /// Rules 1–3: the name is a plain file name, ends with <c>.SIF.GPG</c> or <c>.SIF</c> (case-insensitive), and
    /// has exactly <see cref="ExpectedLength"/> characters. On success it is split into MOL id and timestamp.
    /// </summary>
    /// <remarks>
    /// An encrypted name must carry <c>.SIF</c> before <c>.GPG</c>: the decrypted file keeps the name without
    /// <c>.GPG</c>, and that name has to be a SIF name for the later steps and the generated Central Bank file.
    /// </remarks>
    public static (CbxFileName? Name, WpsRuleFailure? Failure) Check(string fileName, CbxRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsPlainFileName(fileName))
            return (null, new WpsRuleFailure("FILENAME_INVALID", NotPlainNameMessage));

        var encrypted = fileName.EndsWith(GpgSuffix, StringComparison.OrdinalIgnoreCase);
        if (!encrypted && !fileName.EndsWith(SifSuffix, StringComparison.OrdinalIgnoreCase))
            return (null, new WpsRuleFailure("FILENAME_INVALID", ExtensionMessage));

        var expectedLength = ExpectedLength(settings, encrypted);
        if (fileName.Length != expectedLength)
            return (null, new WpsRuleFailure("FILENAME_INVALID", $"File Name Length should be {expectedLength}"));

        var sifFileName = encrypted ? fileName[..^GpgSuffix.Length] : fileName;
        if (!sifFileName.EndsWith(SifSuffix, StringComparison.OrdinalIgnoreCase))
            return (null, new WpsRuleFailure("FILENAME_INVALID", ExtensionMessage));

        var molEstablishmentId = fileName[..settings.MolIdLength];
        if (!molEstablishmentId.All(char.IsAsciiLetterOrDigit))
            return (null, new WpsRuleFailure("FILENAME_INVALID", MolIdInvalidMessage));

        return (new CbxFileName(
            molEstablishmentId,
            fileName.Substring(settings.MolIdLength, DateComponentLength),
            encrypted,
            sifFileName), null);
    }

    /// <summary>
    /// Rules 5–7 on the 12-character <c>yyMMddHHmmss</c> timestamp, in order:
    /// 5. the year is the current year or up to <see cref="CbxRuleSettings.FileDateMaxYearsBack"/> years before
    /// it, and for the current year the month is at most 12; 6. the whole value is a real date and time;
    /// 7. it is earlier than <paramref name="businessNow"/> plus the clock-skew allowance.
    /// </summary>
    /// <param name="businessNow">The current time in the WPS business timezone (see <see cref="SifUploadRules.BusinessNow"/>).</param>
    /// <param name="skewAllowance">How far ahead of <paramref name="businessNow"/> a timestamp may be before it counts as future.</param>
    public static WpsRuleFailure? CheckFileNameDate(
        string dateComponent, DateTime businessNow, TimeSpan skewAllowance, CbxRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (dateComponent is null || dateComponent.Length != DateComponentLength || !dateComponent.All(char.IsAsciiDigit))
            return new WpsRuleFailure("FILEDATE_INVALID", DateFormatMessage);

        // Rule 5. The century comes from the matched year rather than the parser's two-digit-year window,
        // so a file from 1999 can never read as 2099 (or the reverse) around a century boundary.
        var twoDigitYear = int.Parse(dateComponent.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var month = int.Parse(dateComponent.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int? fullYear = null;
        for (var yearsBack = 0; yearsBack <= settings.FileDateMaxYearsBack; yearsBack++)
        {
            var candidate = businessNow.Year - yearsBack;
            if (candidate % 100 == twoDigitYear)
            {
                fullYear = candidate;
                break;
            }
        }

        if (fullYear is null || (fullYear == businessNow.Year && month > 12))
            return new WpsRuleFailure("FILEDATE_INVALID", DateRangeMessage);

        // Rule 6.
        var fourDigitYearValue = fullYear.Value.ToString("D4", CultureInfo.InvariantCulture) + dateComponent[2..];
        if (!DateTime.TryParseExact(fourDigitYearValue, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
            return new WpsRuleFailure("FILEDATE_INVALID", DateFormatMessage);

        // Rule 7. The legacy check is "strictly earlier than now"; with no skew allowance this is exactly that.
        return fileDate >= businessNow + skewAllowance
            ? new WpsRuleFailure("FILEDATE_INVALID", FutureDateMessage)
            : null;
    }

    // Allow-list, not deny-list: a CBX name is only ASCII letters, digits and dots, so path separators, drive
    // and stream markers, control characters, whitespace and Unicode look-alikes (full-width or Arabic-Indic
    // digits, homoglyph dots) are all refused by construction. ".." is refused as well, so the name can never
    // step outside the folder it is resolved in. The length cap bounds the work done on hostile input before
    // the exact-length rule runs. The watcher builds paths from this name, so this runs before any file access.
    private static bool IsPlainFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName)
        && fileName.Length <= MaxFileNameLength
        && fileName.All(c => char.IsAsciiLetterOrDigit(c) || c == '.')
        && !fileName.Contains("..", StringComparison.Ordinal);
}
