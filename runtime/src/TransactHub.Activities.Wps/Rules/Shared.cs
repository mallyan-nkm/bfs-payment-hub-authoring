// Purpose: The words and helpers the WPS rules share (ported from the source's WpsVocabulary, SifUploadRules
// and AccountMasking). Error codes and messages are the channel's contract and are kept exactly.
namespace TransactHub.Activities.Wps.Rules;

using System.Globalization;

/// <summary>A rule that failed: the error code the channel is told, and why.</summary>
public sealed record WpsRuleFailure(string ErrorCode, string Message);

/// <summary>The record tags of a SIF file's lines (SSM 5.6).</summary>
public static class SifRecordTypes
{
    public const string Edr = "EDR";
    public const string Evp = "EVP";
    public const string Scr = "SCR";
}

/// <summary>SIF file-name suffixes and the size limit every SIF reader applies.</summary>
public static class SifFiles
{
    public const string SifSuffix = ".SIF";
    public const string GpgSuffix = ".GPG";
    public const long MaxFileBytes = 64L * 1024 * 1024;
}

/// <summary>Masks an account before it reaches a message, a log or a screen: the first characters and stars.</summary>
public static class AccountMasking
{
    public static string Mask(string? accountOrIban, int visiblePrefixLength = 6)
    {
        if (string.IsNullOrWhiteSpace(accountOrIban) || accountOrIban.Length <= visiblePrefixLength)
            return "****";
        return string.Concat(accountOrIban[..visiblePrefixLength], new string('*', accountOrIban.Length - visiblePrefixLength));
    }
}

/// <summary>The parts of a valid SIF file name.</summary>
public sealed record SifFileName(string MolEstablishmentId, string DateComponent);

/// <summary>The registered corporate a file name resolves to.</summary>
public sealed record SifRegisteredCorporate(string CorporateId, string? CifNumber, string? AccountNumber, string? Email, string? CorporateType = null);

/// <summary>The SIF upload pre-checks every channel applies (ported from the source's SifUploadRules).</summary>
public static class SifUploadRules
{
    /// <summary>The names one SIF can have been submitted under before: as sent, plain, encrypted.</summary>
    public static (string AsSent, string Plain, string Encrypted) FileNameVariants(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        const string encryptedExtension = ".gpg";
        var plain = fileName.EndsWith(encryptedExtension, StringComparison.OrdinalIgnoreCase) ? fileName[..^encryptedExtension.Length] : fileName;
        return (fileName, plain, plain + encryptedExtension);
    }

    /// <summary>Splits a SIF file name into its MOL establishment id and 12-character date: .SIF 29–51 characters, .GPG 33–55.</summary>
    public static (SifFileName? Name, WpsRuleFailure? Failure) ParseFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var extension = fileName.Length >= 4 ? fileName[^4..].ToUpperInvariant() : string.Empty;
        var (minLength, maxLength, trailingLength) = extension switch
        {
            ".SIF" => (29, 51, 16),
            ".GPG" => (33, 55, 20),
            _ => (0, 0, 0),
        };
        if (trailingLength == 0)
            return (null, new WpsRuleFailure("FILENAME_INVALID", $"Unsupported file extension '{extension}'. Expected '.SIF' or '.GPG'."));
        if (fileName.Length < minLength || fileName.Length > maxLength)
            return (null, new WpsRuleFailure("FILENAME_INVALID", $"Filename length {fileName.Length} is outside the valid range ({minLength}-{maxLength}) for a {extension} file."));
        var molIdLength = fileName.Length - trailingLength;
        return (new SifFileName(fileName[..molIdLength], fileName.Substring(molIdLength, 12)), null);
    }

    /// <summary>The file-name timestamp (yyMMddHHmmss, UAE local time) is a real date and not in the future beyond the allowance (SSM 00011/00012).</summary>
    public static WpsRuleFailure? CheckFileNameDate(string dateComponent, DateTime businessNow, TimeSpan skewAllowance, string timeZoneId)
    {
        if (!DateTime.TryParseExact(dateComponent, "yyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
            return new WpsRuleFailure("FILEDATE_INVALID", $"Filename date component '{dateComponent}' is not a valid date/time (expected yyMMddHHmmss).");
        return fileDate > businessNow + skewAllowance
            ? new WpsRuleFailure("FILEDATE_INVALID",
                $"Filename date/time '{fileDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}' is in the future — the current business time is '{businessNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}' ({timeZoneId}).")
            : null;
    }

    /// <summary>The corporate exists, is active and WPS-registered, and any CIF, account or e-mail supplied matches it (003 / 114 / 010 / 165).</summary>
    public static WpsRuleFailure? MatchCorporate(string molEstablishmentId, SifRegisteredCorporate? corporate, string? suppliedCif, string? suppliedAccount, string? suppliedEmail)
    {
        if (string.IsNullOrWhiteSpace(molEstablishmentId))
            return new WpsRuleFailure("003", "MOL Establishment Id could not be extracted from the filename.");
        if (corporate is null)
            return new WpsRuleFailure("003", $"MOL Establishment Id '{molEstablishmentId}' in filename doesn't exist or is not an active, WPS-registered corporate.");
        if (Differs(suppliedCif, corporate.CifNumber))
            return new WpsRuleFailure("114", "Supplied cifNumber does not match the CIF number registered for this corporate's MOL Establishment Id.");
        if (Differs(suppliedAccount, corporate.AccountNumber))
            return new WpsRuleFailure("010", "Supplied accountNo does not match the account number registered for this corporate's MOL Establishment Id.");
        if (Differs(suppliedEmail, corporate.Email))
            return new WpsRuleFailure("165", "Supplied email does not match the email registered for this corporate's MOL Establishment Id.");
        return null;
    }

    static bool Differs(string? supplied, string? registered) =>
        !string.IsNullOrWhiteSpace(supplied) && !string.Equals(supplied.Trim(), registered?.Trim(), StringComparison.OrdinalIgnoreCase);
}
