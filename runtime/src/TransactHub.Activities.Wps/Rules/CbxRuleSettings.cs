// Purpose: The bank's values for the CBX salary-file rules (ported from the source's CbxRuleSettings, US 5001).
// Responsibility: one record that carries every value a bank may set differently for CBX intake and SCR
// validation, so CbxFileNameRules and CbxScrRules hold no bank-specific constant. The values are a bank's
// configuration values (effective-dated versions in the store); the watcher and the upload API both build this record
// from the same rows, so the two always apply the same values. There is deliberately no code default: a
// missing or unparseable parameter is reported by name and the caller refuses to process the file.
namespace TransactHub.Activities.Wps.Rules;

using System.Globalization;

/// <summary>The tenant's CBX rule parameters (design §4.5). An out-of-range value cannot be constructed.</summary>
public sealed record CbxRuleSettings
{
    /// <summary>The MOL id window every SIF file name already uses (<see cref="SifUploadRules.ParseFileName"/>).</summary>
    public const int MinMolIdLength = 13;
    public const int MaxMolIdLength = 35;

    /// <summary>The file-name year has two digits, so a wider window than 99 years would be ambiguous.</summary>
    public const int MaxFileDateYearsBack = 99;

    /// <summary>The longest IBAN (ISO 13616).</summary>
    public const int MaxAccountMatchLength = 34;

    /// <param name="molIdLength">Length of the MOL establishment id at the start of a CBX file name (the UAE default is 13).</param>
    /// <param name="fileDateMaxYearsBack">How many years before the current one a file-name timestamp may fall in (the UAE default is 1).</param>
    /// <param name="scrDebitAccountRequired">Rule 82: SCR field 10 (debit account) must be present.</param>
    /// <param name="scrDebitAccountRegisteredCheck">Rule 83: SCR field 10 must be one of the corporate's registered accounts.</param>
    /// <param name="accountMatchLength">Rule 83: values longer than this are compared on their rightmost characters only (the UAE default is 15).</param>
    public CbxRuleSettings(
        int molIdLength, int fileDateMaxYearsBack, bool scrDebitAccountRequired, bool scrDebitAccountRegisteredCheck, int accountMatchLength)
    {
        MolIdLength = molIdLength;
        FileDateMaxYearsBack = fileDateMaxYearsBack;
        ScrDebitAccountRequired = scrDebitAccountRequired;
        ScrDebitAccountRegisteredCheck = scrDebitAccountRegisteredCheck;
        AccountMatchLength = accountMatchLength;
    }

    /// <summary>Length of the leading MOL establishment id, <see cref="MinMolIdLength"/>–<see cref="MaxMolIdLength"/>.</summary>
    public int MolIdLength
    {
        get;
        init => field = InRange(value, MinMolIdLength, MaxMolIdLength);
    }

    /// <summary>Years before the current one a file-name timestamp may fall in, 0–<see cref="MaxFileDateYearsBack"/>.</summary>
    public int FileDateMaxYearsBack
    {
        get;
        init => field = InRange(value, 0, MaxFileDateYearsBack);
    }

    /// <summary>Rule 82: SCR field 10 (debit account) must be present.</summary>
    public bool ScrDebitAccountRequired { get; init; }

    /// <summary>Rule 83: SCR field 10 must be one of the corporate's registered accounts.</summary>
    public bool ScrDebitAccountRegisteredCheck { get; init; }

    /// <summary>Rule 83 right-N comparison length, 1–<see cref="MaxAccountMatchLength"/>.</summary>
    public int AccountMatchLength
    {
        get;
        init => field = InRange(value, 1, MaxAccountMatchLength);
    }

    /// <summary>The configuration parameter of each value (unchanged from the source, so a bank's existing values carry over).</summary>
    public static class ParamNames
    {
        public const string MolIdLength = "CbxMolIdLength";
        public const string FileDateMaxYearsBack = "CbxFileDateMaxYearsBack";
        public const string ScrDebitAccountRequired = "CbxScrDebitAccountRequired";
        public const string ScrDebitAccountRegisteredCheck = "CbxScrDebitAccountRegisteredCheck";
        public const string AccountMatchLength = "CbxAccountMatchLength";

        /// <summary>Every parameter the record needs, in a stable order.</summary>
        public static IReadOnlyList<string> All { get; } =
            [MolIdLength, FileDateMaxYearsBack, ScrDebitAccountRequired, ScrDebitAccountRegisteredCheck, AccountMatchLength];
    }

    /// <summary>
    /// Builds the settings from raw parameter values keyed by <see cref="ParamNames"/>. Returns the name of
    /// the first parameter that is missing, unparseable or out of range instead of substituting a default.
    /// </summary>
    /// <remarks>
    /// Integers are invariant-culture ASCII digits with no sign; booleans are <c>true</c>/<c>false</c>
    /// (case-insensitive). Surrounding whitespace is ignored. Ranges are the ones the properties enforce.
    /// </remarks>
    public static (CbxRuleSettings? Settings, string? InvalidParam) FromParameters(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!TryInt(values, ParamNames.MolIdLength, MinMolIdLength, MaxMolIdLength, out var molIdLength))
            return (null, ParamNames.MolIdLength);
        if (!TryInt(values, ParamNames.FileDateMaxYearsBack, 0, MaxFileDateYearsBack, out var yearsBack))
            return (null, ParamNames.FileDateMaxYearsBack);
        if (!TryBool(values, ParamNames.ScrDebitAccountRequired, out var debitRequired))
            return (null, ParamNames.ScrDebitAccountRequired);
        if (!TryBool(values, ParamNames.ScrDebitAccountRegisteredCheck, out var registeredCheck))
            return (null, ParamNames.ScrDebitAccountRegisteredCheck);
        if (!TryInt(values, ParamNames.AccountMatchLength, 1, MaxAccountMatchLength, out var matchLength))
            return (null, ParamNames.AccountMatchLength);

        return (new CbxRuleSettings(molIdLength, yearsBack, debitRequired, registeredCheck, matchLength), null);
    }

    private static int InRange(int value, int min, int max) =>
        value >= min && value <= max
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Must be between {min} and {max}.");

    private static bool TryInt(IReadOnlyDictionary<string, string?> values, string name, int min, int max, out int value)
    {
        value = 0;
        return values.TryGetValue(name, out var raw)
            && int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max;
    }

    private static bool TryBool(IReadOnlyDictionary<string, string?> values, string name, out bool value)
    {
        value = false;
        return values.TryGetValue(name, out var raw) && bool.TryParse(raw?.Trim(), out value);
    }
}
