// Purpose: Builds the Central Bank SIF from the stored records (ported from the source's CbxSifWriter, US 5001,
// design §4.3, 2026-10-06).
// Responsibility: records in, SIF text out: each EDR line (received order) followed by that employee's EVP
// lines, then one SCR line last; comma-separated, every line ending in CRLF. Pure and deterministic: the creation date and time
// come from the caller (captured once and stored), so a retried wps.GenerateCbxSif rebuilds byte-identical
// content and the same hash. No I/O; reading wps.Sif_Edr / Sif_Evp / Sif_Scr and writing the file belong to
// the activity. The padding widths (person id 14, establishment id 13, account 23) and the 10-field SCR are
// the Central Bank SIF format, the same for every bank, so they are constants here, not tenant parameters.
namespace TransactHub.Activities.Wps.Sif;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TransactHub.Activities.Wps.Rules;

/// <summary>One stored EDR, as the generator needs it.</summary>
/// <param name="Id">The row id: the tie-breaker when two rows share an <paramref name="EdrOrderNo"/>.</param>
/// <param name="EdrOrderNo">Position in the received file. The Central Bank's NAK DER records refer to it.</param>
public sealed record CbxSifEmployee(
    long Id, int EdrOrderNo, string PersonId, string AgentRoutingCode, string AccountNo,
    DateOnly PayStartDate, DateOnly PayEndDate, int DaysInPeriod, decimal FixedPay, decimal VariablePay, decimal DaysOnLeave);

/// <summary>One stored EVP. An absent pay component is passed as zero.</summary>
public sealed record CbxSifVariablePay(
    long Id, string PersonId, string RoutingCode, decimal Housing, decimal Conveyance, decimal Medical,
    decimal AnnualPassage, decimal Overtime, decimal Other, decimal LeaveEncashment);

/// <summary>The SCR values that do not come from the EDRs.</summary>
/// <param name="BankRoutingCode">The bank's routing code parameter (the value SCR rule 66 validates).</param>
/// <param name="CreatedAt">Generation time, captured once and stored, written as creation date and time.</param>
/// <param name="SalaryMonthYear">The received SCR's <c>MMYYYY</c>.</param>
/// <param name="EmployerReference">SCR field 10: the corporate identifier, never the received debit account (Q4).</param>
public sealed record CbxSifControl(
    string EstablishmentId, string BankRoutingCode, DateTime CreatedAt, string SalaryMonthYear, string Currency, string EmployerReference);

/// <summary>A generated SIF: its text, the SHA-256 of <see cref="CbxSifWriter.Encode"/>(<paramref name="Content"/>), and its totals.</summary>
public sealed record CbxSifDocument(string Content, string Sha256, int EdrCount, int EvpCount, decimal TotalAmount);

/// <summary>Why a SIF could not be generated: <see cref="CbxSifWriter.NoRecordsCode"/> or <see cref="CbxSifWriter.InvalidRecordCode"/>.</summary>
public sealed record CbxSifFailure(string ErrorCode, string Message);

/// <summary>Pure builder for the CBX-generated Central Bank SIF.</summary>
public static class CbxSifWriter
{
    public const string NoRecordsCode = "SIF_GENERATION_NO_RECORDS";
    public const string InvalidRecordCode = "SIF_GENERATION_INVALID_RECORD";

    private const int PersonIdWidth = 14;
    private const int EstablishmentIdWidth = 13;
    private const int AccountNoWidth = 23;
    private const string LineEnd = "\r\n";
    private const string SifSuffix = SifFiles.SifSuffix;
    private const string GpgSuffix = SifFiles.GpgSuffix;

    /// <summary>
    /// Builds the SIF. Fails with <see cref="NoRecordsCode"/> when there is no EDR (an empty file is never
    /// produced) and with <see cref="InvalidRecordCode"/> naming the record and field when a value cannot be
    /// written as-is: blank, containing a comma or line break, negative, or a malformed salary month.
    /// </summary>
    /// <remarks>
    /// Amounts are rounded to two decimals (midpoint away from zero) per line, and the SCR total is the sum of
    /// the rounded <c>FixedPay + VariablePay</c> as written, so the control total always equals the lines.
    /// Identifiers shorter than their width are left-padded with zeros and never truncated.
    /// </remarks>
    public static (CbxSifDocument? Document, CbxSifFailure? Failure) Build(
        IReadOnlyList<CbxSifEmployee> employees, IReadOnlyList<CbxSifVariablePay> variablePays, CbxSifControl control)
    {
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(variablePays);
        ArgumentNullException.ThrowIfNull(control);

        if (employees.Count == 0)
            return (null, new CbxSifFailure(NoRecordsCode, "No EDR records were found for the transaction; a SIF without employees is never generated."));

        var failure = ValidateControl(control);
        if (failure is not null)
            return (null, failure);

        var orderedEmployees = employees.OrderBy(e => e.EdrOrderNo).ThenBy(e => e.Id).ToList();
        foreach (var edr in orderedEmployees)
        {
            failure = ValidateEmployee(edr);
            if (failure is not null)
                return (null, failure);
        }

        // An EVP only breaks down the variable pay of an employee the file pays; one with no EDR would be
        // rejected by the Central Bank, so it is refused here. Ids are compared as written (padded).
        var paidPersonIds = orderedEmployees.Select(e => Pad(e.PersonId, PersonIdWidth)).ToHashSet(StringComparer.Ordinal);
        var orderedVariablePays = variablePays.OrderBy(v => v.Id).ToList();
        foreach (var evp in orderedVariablePays)
        {
            failure = ValidateVariablePay(evp)
                ?? (paidPersonIds.Contains(Pad(evp.PersonId, PersonIdWidth))
                    ? null
                    : Invalid($"EVP {evp.Id}", "EmployeeId", "has no EDR in the file"));
            if (failure is not null)
                return (null, failure);
        }

        var variablePaysByPersonId = orderedVariablePays.ToLookup(v => Pad(v.PersonId, PersonIdWidth), StringComparer.Ordinal);
        var content = new StringBuilder();
        var total = 0m;
        foreach (var edr in orderedEmployees)
        {
            var personId = Pad(edr.PersonId, PersonIdWidth);
            var fixedPay = Money(edr.FixedPay);
            var variablePay = Money(edr.VariablePay);
            total += fixedPay + variablePay;
            // Days on Leave is always 0 in the generated SIF (the source's CBX design, as the bank sends it); the
            // uploaded value stays validated and stored.
            AppendLine(content,
                SifRecordTypes.Edr, personId, edr.AgentRoutingCode.Trim(), Pad(edr.AccountNo, AccountNoWidth),
                Date(edr.PayStartDate), Date(edr.PayEndDate), edr.DaysInPeriod.ToString(CultureInfo.InvariantCulture),
                Amount(fixedPay), Amount(variablePay), "0");

            foreach (var evp in variablePaysByPersonId[personId])
            {
                AppendLine(content,
                    SifRecordTypes.Evp, personId, evp.RoutingCode.Trim(),
                    Amount(Money(evp.Housing)), Amount(Money(evp.Conveyance)), Amount(Money(evp.Medical)),
                    Amount(Money(evp.AnnualPassage)), Amount(Money(evp.Overtime)), Amount(Money(evp.Other)),
                    Amount(Money(evp.LeaveEncashment)));
            }
        }

        AppendLine(content,
            SifRecordTypes.Scr, Pad(control.EstablishmentId, EstablishmentIdWidth), control.BankRoutingCode.Trim(),
            control.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            control.CreatedAt.ToString("HHmm", CultureInfo.InvariantCulture),
            control.SalaryMonthYear.Trim(), employees.Count.ToString(CultureInfo.InvariantCulture),
            Amount(total), control.Currency.Trim(), control.EmployerReference.Trim());

        var text = content.ToString();
        return (new CbxSifDocument(text, Convert.ToHexString(SHA256.HashData(Encode(text))), employees.Count, variablePays.Count, total), null);
    }

    /// <summary>The bytes written to disk and uploaded: UTF-8 without a byte-order mark (the content is ASCII).</summary>
    public static byte[] Encode(string content) => Encoding.UTF8.GetBytes(content);

    /// <summary>
    /// The generated SIF's name: the received name without <c>.GPG</c>, i.e. <c>{MOL}{yyMMddHHmmss}.SIF</c>. The
    /// extension is always written upper-case: the Central Bank's ACK/NAK names embed this name, and an SFTP
    /// server or a correlation lookup may compare it case-sensitively.
    /// </summary>
    /// <exception cref="ArgumentException">The name does not end in <c>.SIF</c> or <c>.SIF.GPG</c>.</exception>
    public static string FileNameFor(string receivedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receivedFileName);
        var sifName = receivedFileName.EndsWith(GpgSuffix, StringComparison.OrdinalIgnoreCase)
            ? receivedFileName[..^GpgSuffix.Length]
            : receivedFileName;
        if (!sifName.EndsWith(SifSuffix, StringComparison.OrdinalIgnoreCase) || sifName.Length == SifSuffix.Length)
            throw new ArgumentException("Expected a .SIF or .SIF.GPG file name.", nameof(receivedFileName));
        return sifName[..^SifSuffix.Length] + SifSuffix;
    }

    /// <summary>The name used when <see cref="FileNameFor"/> was already taken by a generated SIF: <c>{MOL}{now yyMMddHHmmss}.SIF</c>.</summary>
    /// <exception cref="ArgumentException">The MOL id is not ASCII letters and digits.</exception>
    public static string FallbackFileName(string molEstablishmentId, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(molEstablishmentId);
        var molId = molEstablishmentId.Trim();
        if (!molId.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("The MOL establishment id must be ASCII letters and digits.", nameof(molEstablishmentId));
        return molId + now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + SifSuffix;
    }

    private static CbxSifFailure? ValidateControl(CbxSifControl c) =>
        Text(SifRecordTypes.Scr, "EstablishmentId", c.EstablishmentId)
        ?? Text(SifRecordTypes.Scr, "BankRoutingCode", c.BankRoutingCode)
        ?? Text(SifRecordTypes.Scr, "Currency", c.Currency)
        ?? Text(SifRecordTypes.Scr, "EmployerReference", c.EmployerReference)
        ?? Text(SifRecordTypes.Scr, "SalaryMonthYear", c.SalaryMonthYear)
        ?? (IsSalaryMonthYear(c.SalaryMonthYear.Trim())
            ? null
            : Invalid(SifRecordTypes.Scr, "SalaryMonthYear", "must be MMYYYY with a month from 01 to 12"));

    private static CbxSifFailure? ValidateEmployee(CbxSifEmployee e)
    {
        var record = $"EDR {e.EdrOrderNo}";
        return Text(record, "EmployeeId", e.PersonId)
            ?? Text(record, "AgentRoutingCode", e.AgentRoutingCode)
            ?? Text(record, "BeneficiaryAccount", e.AccountNo)
            ?? NonNegative(record, "DaysInPeriod", e.DaysInPeriod)
            ?? NonNegative(record, "FixedPay", e.FixedPay)
            ?? NonNegative(record, "VariablePay", e.VariablePay)
            ?? NonNegative(record, "DaysOnLeave", e.DaysOnLeave);
    }

    private static CbxSifFailure? ValidateVariablePay(CbxSifVariablePay v)
    {
        var record = $"EVP {v.Id}";
        return Text(record, "EmployeeId", v.PersonId)
            ?? Text(record, "RoutingCode", v.RoutingCode)
            ?? NonNegative(record, "Housing", v.Housing)
            ?? NonNegative(record, "Conveyance", v.Conveyance)
            ?? NonNegative(record, "Medical", v.Medical)
            ?? NonNegative(record, "AnnualPassage", v.AnnualPassage)
            ?? NonNegative(record, "Overtime", v.Overtime)
            ?? NonNegative(record, "Other", v.Other)
            ?? NonNegative(record, "LeaveEncashment", v.LeaveEncashment);
    }

    // Allow-list: printable ASCII except the comma. A comma or line break would shift the columns or split the
    // line, so the Central Bank would read a different record than the one stored; any other character outside
    // printable ASCII (control, non-ASCII, look-alike) is not part of the SIF format and would change the bytes
    // the hash is taken over.
    private static CbxSifFailure? Text(string record, string field, string? value) =>
        string.IsNullOrWhiteSpace(value) ? Invalid(record, field, "is missing")
        : value.IndexOfAny([',', '\r', '\n']) >= 0 ? Invalid(record, field, "contains a comma or line break")
        : !value.All(c => c is >= ' ' and <= '~') ? Invalid(record, field, "contains a character the SIF format does not allow")
        : null;

    private static CbxSifFailure? NonNegative(string record, string field, decimal value) =>
        value < 0 ? Invalid(record, field, "is negative") : null;

    private static CbxSifFailure Invalid(string record, string field, string reason) =>
        new(InvalidRecordCode, $"{record}: {field} {reason}.");

    private static bool IsSalaryMonthYear(string value) =>
        value.Length == 6
        && value.All(char.IsAsciiDigit)
        && int.Parse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture) is >= 1 and <= 12;

    private static void AppendLine(StringBuilder content, params string[] fields) =>
        content.Append(string.Join(',', fields)).Append(LineEnd);

    private static string Pad(string value, int width) => value.Trim().PadLeft(width, '0');

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
