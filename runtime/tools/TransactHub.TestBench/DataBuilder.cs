using System.Globalization;
using System.Numerics;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TransactHub.TestBench;

/// <summary>What the builder made: the data, its file name, and what it was built from.</summary>
public sealed record Built(string Text, string Name, string Template, JsonObject Recipe, string[] Injected, string? SuggestedBench);

/// <summary>
/// Builds synthetic test data from a template and parameters, in the factory's formats (WPS SIF and PIF as the
/// factory's intake maps read them, ISO 20022 pain.001 and pacs.008), with the bank's values from its folder in
/// content-configurations (its WPS routing code), so the data is valid for that bank unless an error is injected.
/// Everything is generated: accounts use a reserved bank code, ids carry a synthetic prefix, names start "TB ".
/// </summary>
public sealed class DataBuilder(Home home)
{
    public static readonly string[] Templates = ["wps-sif", "wps-pif", "pain.001", "pacs.008"];

    /// <summary>The injections each template knows, with what each is meant to make happen.</summary>
    public static readonly Dictionary<string, string> Injections = new()
    {
        ["invalid-iban"] = "the first record's IBAN has wrong check digits: the record is refused",
        ["bad-control-total"] = "the control record's total is not the sum of the records",
        ["bad-record-count"] = "the control record's count is not the number of records",
        ["bad-pay-period"] = "the first record's pay period ends before it starts",
        ["bad-employer-id"] = "the employer id is not thirteen digits",
        ["bad-currency"] = "the currency is not AED",
        ["duplicate-person-id"] = "the second record repeats the first record's person id",
        ["wrong-agent"] = "a PIF addressed to another bank",
        ["sanctioned-name"] = "a payee named as the screening test list's hit; the simulators decide by scenario, so the sanctions-hit bench goes with it (Issue I-96)",
        ["duplicate-file"] = "the same file again: the payment record finds it, so the duplicate bench goes with it",
    };

    /// <summary>The bank code no UAE bank has: every account the builder makes is under it.</summary>
    public const string ReservedBankCode = "999";
    public const string SyntheticNamePrefix = "TB ";
    public const string SanctionedTestName = "TB SANCTIONS TEST HIT";

    /// <summary>The WPS routing code a bank's configuration set gives it (uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes).</summary>
    public string? RoutingCode(string tenant)
    {
        var banks = Path.Combine(home.Repo, "content-configurations", "banks");
        if (!Directory.Exists(banks)) return null;
        foreach (var dir in Directory.EnumerateDirectories(banks).Order(StringComparer.Ordinal))
        {
            var f = Path.Combine(dir, "configuration-set.yaml");
            if (!File.Exists(f)) continue;
            var cs = Yaml.Load(f);
            if (J.S(cs, "tenant") != tenant && Path.GetFileName(dir) != tenant) continue;
            foreach (var v in (cs["values"] as JsonArray ?? []).OfType<JsonObject>())
                if (J.S(v, "parameter") == "uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes") return J.Strings(v["value"]).FirstOrDefault();
        }
        return null;
    }

    public IEnumerable<string> Tenants()
    {
        var banks = Path.Combine(home.Repo, "content-configurations", "banks");
        return Directory.Exists(banks) ? Directory.EnumerateDirectories(banks).Select(d => Path.Combine(d, "configuration-set.yaml")).Where(File.Exists).Select(f => J.S(Yaml.Load(f), "tenant")!).Order(StringComparer.Ordinal) : [];
    }

    public Built Build(JsonObject recipe)
    {
        var template = J.S(recipe, "template") ?? throw new BenchError($"A recipe names a template: {string.Join(", ", Templates)}");
        var tenant = J.S(recipe, "tenant") ?? "example-bank";
        var inject = J.Strings(recipe["inject"]).ToArray();
        foreach (var i in inject) if (!Injections.ContainsKey(i)) throw new BenchError($"No injection {i}: the builder injects {string.Join(", ", Injections.Keys)}");
        var p = new Params(recipe, tenant, RoutingCode(tenant));
        var (text, name) = template switch
        {
            "wps-sif" => Sif(p, inject),
            "wps-pif" => Pif(p, inject),
            "pain.001" => Pain001(p, inject),
            "pacs.008" => Pacs008(p, inject),
            _ => throw new BenchError($"No template {template}: {string.Join(", ", Templates)}"),
        };
        var bench = inject.Contains("sanctioned-name") ? "sanctions-hit" : inject.Contains("duplicate-file") ? (template == "wps-pif" ? "wps-inward-duplicate" : "wps-duplicate") : null;
        return new Built(text, name, template, (JsonObject)recipe.DeepClone(), inject, bench);
    }

    sealed class Params
    {
        public readonly int Lines, Seed;
        public readonly string Tenant, Routing, Employer, Currency, Agent, Reference;
        public readonly DateOnly Date;
        public readonly decimal? Amount;
        public readonly decimal Min, Max;
        public readonly TimeOnly Time;
        readonly Random rnd;

        public Params(JsonObject r, string tenant, string? routing)
        {
            Tenant = tenant;
            Lines = (int)(J.Long(r["lines"]) ?? 1);
            if (Lines < 1 || Lines > 100000) throw new BenchError("lines is from 1 to 100000");
            Seed = (int)(J.Long(r["seed"]) ?? 1);
            rnd = new Random(Seed);
            Routing = J.S(r, "routingCode") ?? routing ?? throw new BenchError($"The tenant {tenant} has no WPS routing code in its configuration set; give routingCode");
            Employer = J.S(r, "employerId") ?? $"00000{rnd.Next(10000000, 99999999)}";
            Currency = J.S(r, "currency") ?? "AED";
            Agent = J.S(r, "agentRoutingCode") ?? "999000101";
            Date = DateOnly.Parse(J.S(r, "valueDate") ?? J.S(r, "date") ?? "2026-11-17", CultureInfo.InvariantCulture);
            Time = TimeOnly.Parse(J.S(r, "time") ?? "08:30", CultureInfo.InvariantCulture);
            Reference = J.S(r, "reference") ?? $"TB-{Seed:0000}";
            if (r["amount"] is JsonValue a) Amount = decimal.Parse(a.ToJsonString().Trim('"'), CultureInfo.InvariantCulture);
            var dist = r["amounts"] as JsonObject;
            Min = dist?["min"] is { } mn ? decimal.Parse(mn.ToJsonString(), CultureInfo.InvariantCulture) : 500m;
            Max = dist?["max"] is { } mx ? decimal.Parse(mx.ToJsonString(), CultureInfo.InvariantCulture) : 5000m;
            if (Min <= 0 || Max < Min) throw new BenchError("amounts.min is above zero and amounts.max is not below it");
        }

        // Amounts are quarters of a dirham: the factory's rules add money as binary floating point (Issue I-96), and a
        // sum of quarters is exact there, so a generated file is valid unless an error is injected on purpose.
        public decimal NextAmount() => Amount ?? Quarter(Min + (decimal)rnd.NextDouble() * (Max - Min));
        public static decimal Quarter(decimal d) => Math.Max(0.25m, Math.Round(d * 4m, MidpointRounding.AwayFromZero) / 4m);
        public string Account(int i) => Iban("AE", ReservedBankCode + (Seed % 1000).ToString("000", CultureInfo.InvariantCulture) + (i + 1).ToString("0000000000000", CultureInfo.InvariantCulture));
        public string Person(int i) => "0000" + (Seed % 100).ToString("00", CultureInfo.InvariantCulture) + (i + 1).ToString("00000000", CultureInfo.InvariantCulture);
        public string Month => Date.AddMonths(-1).ToString("MMyyyy", CultureInfo.InvariantCulture);
        public DateOnly PeriodStart => new DateOnly(Date.Year, Date.Month, 1).AddMonths(-1);
        public DateOnly PeriodEnd => new DateOnly(Date.Year, Date.Month, 1).AddDays(-1);
    }

    static string F(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);
    static string D(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An IBAN with valid check digits (ISO 13616 mod 97).</summary>
    public static string Iban(string country, string bban)
    {
        var check = 98 - Mod97(bban + country + "00");
        return $"{country}{check:00}{bban}";
    }

    public static int Mod97(string s)
    {
        var digits = new StringBuilder();
        foreach (var c in s) digits.Append(char.IsLetter(c) ? (char.ToUpperInvariant(c) - 'A' + 10).ToString(CultureInfo.InvariantCulture) : c.ToString());
        return (int)(BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture) % 97);
    }

    public static bool IbanValid(string iban) => iban.Length > 4 && Mod97(iban[4..] + iban[..4]) == 1;

    static string Corrupt(string iban) => iban[..2] + ((int.Parse(iban[2..4], CultureInfo.InvariantCulture) + 1) % 100).ToString("00", CultureInfo.InvariantCulture) + iban[4..];

    (string, string) Sif(Params p, string[] inject)
    {
        var employer = inject.Contains("bad-employer-id") ? p.Employer[1..] : p.Employer;
        var ccy = inject.Contains("bad-currency") ? "USD" : p.Currency;
        var edr = new List<string>();
        decimal total = 0;
        for (var i = 0; i < p.Lines; i++)
        {
            var amount = p.NextAmount();
            var fixedPay = Params.Quarter(amount * 0.8m);
            var variable = amount - fixedPay;
            total += amount;
            var iban = p.Account(i);
            if (i == 0 && inject.Contains("invalid-iban")) iban = Corrupt(iban);
            var person = i == 1 && inject.Contains("duplicate-person-id") ? p.Person(0) : p.Person(i);
            var (start, end) = i == 0 && inject.Contains("bad-pay-period") ? (p.PeriodEnd, p.PeriodStart) : (p.PeriodStart, p.PeriodEnd);
            edr.Add($"EDR,{person},{p.Agent},{iban},{D(start)},{D(end)},{end.DayNumber - start.DayNumber + 1},{F(fixedPay)},{F(variable)},0");
        }
        var count = inject.Contains("bad-record-count") ? p.Lines + 1 : p.Lines;
        var sum = inject.Contains("bad-control-total") ? total + 1m : total;
        var name = $"{employer}{p.Date:yyMMdd}{p.Time:HHmm}00.sif";
        var sb = new StringBuilder();
        sb.Append($"# file: {name}\n");
        sb.Append($"SCR,{employer},{p.Routing},{D(p.Date)},{p.Time:HHmm},{p.Month},{count},{F(sum)},{ccy},{p.Reference}\n");
        foreach (var l in edr) sb.Append(l).Append('\n');
        return (sb.ToString(), name);
    }

    (string, string) Pif(Params p, string[] inject)
    {
        var agent = inject.Contains("wrong-agent") ? "999000202" : p.Routing;
        var ccy = inject.Contains("bad-currency") ? "USD" : p.Currency;
        var pdr = new List<string>();
        decimal total = 0;
        for (var i = 0; i < p.Lines; i++)
        {
            var amount = p.NextAmount();
            var fixedPay = Params.Quarter(amount * 0.8m);
            total += amount;
            var iban = p.Account(i);
            if (i == 0 && inject.Contains("invalid-iban")) iban = Corrupt(iban);
            var person = i == 1 && inject.Contains("duplicate-person-id") ? p.Person(0) : p.Person(i);
            var (start, end) = i == 0 && inject.Contains("bad-pay-period") ? (p.PeriodEnd, p.PeriodStart) : (p.PeriodStart, p.PeriodEnd);
            pdr.Add($"PDR,{person},{agent},{iban},{D(start)},{D(end)},{end.DayNumber - start.DayNumber + 1},{F(fixedPay)},{F(amount - fixedPay)},0");
        }
        var count = inject.Contains("bad-record-count") ? p.Lines + 1 : p.Lines;
        var sum = inject.Contains("bad-control-total") ? total + 1m : total;
        var employer = inject.Contains("bad-employer-id") ? p.Employer[1..] : p.Employer;
        var name = $"{agent}{p.Date:yyMMdd}{p.Time:HHmm}00.pif";
        var sb = new StringBuilder();
        sb.Append($"# file: {name}\n");
        sb.Append($"PCR,{agent},{employer},{agent},{D(p.Date)},{p.Time:HHmm},{p.Month},{count},{F(sum)},{ccy},PIF-{p.Seed:0000}\n");
        foreach (var l in pdr) sb.Append(l).Append('\n');
        return (sb.ToString(), name);
    }

    static string X(string s) => SecurityElement.Escape(s);

    (string, string) Pain001(Params p, string[] inject)
    {
        var ccy = inject.Contains("bad-currency") ? "USD" : p.Currency;
        var tx = new StringBuilder();
        decimal total = 0;
        for (var i = 0; i < p.Lines; i++)
        {
            var amount = p.NextAmount();
            total += amount;
            var iban = p.Account(i + 1);
            if (i == 0 && inject.Contains("invalid-iban")) iban = Corrupt(iban);
            var payee = i == 0 && inject.Contains("sanctioned-name") ? SanctionedTestName : $"{SyntheticNamePrefix}Synthetic Payee {i + 1:0000}";
            tx.Append($"""
                      <CdtTrfTxInf>
                        <PmtId><InstrId>{p.Reference}-{i + 1:0000}</InstrId><EndToEndId>{p.Reference}-E2E-{i + 1:0000}</EndToEndId></PmtId>
                        <Amt><InstdAmt Ccy="{ccy}">{F(amount)}</InstdAmt></Amt>
                        <CdtrAgt><FinInstnId><ClrSysMmbId><MmbId>{p.Agent}</MmbId></ClrSysMmbId></FinInstnId></CdtrAgt>
                        <Cdtr><Nm>{X(payee)}</Nm></Cdtr>
                        <CdtrAcct><Id><IBAN>{iban}</IBAN></Id></CdtrAcct>
                      </CdtTrfTxInf>

                """);
        }
        var count = inject.Contains("bad-record-count") ? p.Lines + 1 : p.Lines;
        var sum = inject.Contains("bad-control-total") ? total + 1m : total;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- Synthetic test data from the TransactHub test bench. No real party, account or bank. -->
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:pain.001.001.09">
              <CstmrCdtTrfInitn>
                <GrpHdr>
                  <MsgId>{p.Reference}</MsgId>
                  <CreDtTm>{D(p.Date)}T{p.Time:HH:mm}:00</CreDtTm>
                  <NbOfTxs>{count}</NbOfTxs>
                  <CtrlSum>{F(sum)}</CtrlSum>
                  <InitgPty><Nm>{SyntheticNamePrefix}Synthetic Employer {p.Seed:00}</Nm></InitgPty>
                </GrpHdr>
                <PmtInf>
                  <PmtInfId>{p.Reference}-PMT</PmtInfId>
                  <PmtMtd>TRF</PmtMtd>
                  <NbOfTxs>{count}</NbOfTxs>
                  <CtrlSum>{F(sum)}</CtrlSum>
                  <PmtTpInf><CtgyPurp><Cd>SALA</Cd></CtgyPurp></PmtTpInf>
                  <ReqdExctnDt><Dt>{D(p.Date)}</Dt></ReqdExctnDt>
                  <Dbtr><Nm>{SyntheticNamePrefix}Synthetic Employer {p.Seed:00}</Nm></Dbtr>
                  <DbtrAcct><Id><IBAN>{p.Account(0)}</IBAN></Id></DbtrAcct>
                  <DbtrAgt><FinInstnId><ClrSysMmbId><MmbId>{p.Routing}</MmbId></ClrSysMmbId></FinInstnId></DbtrAgt>
            {tx}    </PmtInf>
              </CstmrCdtTrfInitn>
            </Document>

            """;
        return (xml, $"{p.Reference}.pain001.xml");
    }

    (string, string) Pacs008(Params p, string[] inject)
    {
        var ccy = inject.Contains("bad-currency") ? "USD" : p.Currency;
        var amount = p.NextAmount();
        var iban = p.Account(1);
        if (inject.Contains("invalid-iban")) iban = Corrupt(iban);
        var payee = inject.Contains("sanctioned-name") ? SanctionedTestName : $"{SyntheticNamePrefix}Synthetic Payee 0001";
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!-- Synthetic test data from the TransactHub test bench. No real party, account or bank. -->
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08">
              <FIToFICstmrCdtTrf>
                <GrpHdr>
                  <MsgId>{p.Reference}</MsgId>
                  <CreDtTm>{D(p.Date)}T{p.Time:HH:mm}:00</CreDtTm>
                  <NbOfTxs>{(inject.Contains("bad-record-count") ? 2 : 1)}</NbOfTxs>
                  <SttlmInf><SttlmMtd>CLRG</SttlmMtd></SttlmInf>
                </GrpHdr>
                <CdtTrfTxInf>
                  <PmtId><InstrId>{p.Reference}-0001</InstrId><EndToEndId>{p.Reference}-E2E-0001</EndToEndId><TxId>{p.Reference}-TX-0001</TxId></PmtId>
                  <IntrBkSttlmAmt Ccy="{ccy}">{F(amount)}</IntrBkSttlmAmt>
                  <IntrBkSttlmDt>{D(p.Date)}</IntrBkSttlmDt>
                  <ChrgBr>SLEV</ChrgBr>
                  <Dbtr><Nm>{SyntheticNamePrefix}Synthetic Payer 0001</Nm></Dbtr>
                  <DbtrAcct><Id><IBAN>{p.Account(0)}</IBAN></Id></DbtrAcct>
                  <DbtrAgt><FinInstnId><ClrSysMmbId><MmbId>{p.Agent}</MmbId></ClrSysMmbId></FinInstnId></DbtrAgt>
                  <CdtrAgt><FinInstnId><ClrSysMmbId><MmbId>{p.Routing}</MmbId></ClrSysMmbId></FinInstnId></CdtrAgt>
                  <Cdtr><Nm>{X(payee)}</Nm></Cdtr>
                  <CdtrAcct><Id><IBAN>{iban}</IBAN></Id></CdtrAcct>
                </CdtTrfTxInf>
              </FIToFICstmrCdtTrf>
            </Document>

            """;
        return (xml, $"{p.Reference}.pacs008.xml");
    }
}

/// <summary>A finding of the format check or the synthetic-data check.</summary>
public sealed record Finding(string Code, string Where, string Message)
{
    public JsonObject Json() => new() { ["code"] = Code, ["where"] = Where, ["message"] = Message };
}

/// <summary>
/// The format check of the test bench: the structure the factory's intake maps read and the record rules of the
/// salary and payment files, checked before a run. The scheme's own rules run on the interpreter
/// (<c>testbench data validate --rules</c>); this is the quick check, not the scheme's word.
/// </summary>
public static class Format
{
    static readonly Regex Digits13 = new(@"^\d{13}$"), Digits9 = new(@"^\d{9}$"), Digits14 = new(@"^\d{14}$"), Month = new(@"^(0[1-9]|1[0-2])\d{4}$");

    public static List<Finding> Check(Input input, string? tenantRouting = null)
    {
        var f = new List<Finding>();
        if (input.Payload is null)
        {
            if (input.Context?["header"] is not JsonObject) f.Add(new("TB-F01", "context", "No header"));
            foreach (var (iban, where) in Ibans(input.Context)) if (!DataBuilder.IbanValid(iban)) f.Add(new("TB-F10", where, "IBAN check digits are wrong"));
            return f;
        }
        var lines = input.Payload.Split('\n').Select((l, i) => (l: l.Trim(), n: i + 1)).Where(x => x.l.Length > 0 && !x.l.StartsWith('#')).ToList();
        var fileName = Regex.Match(input.Payload, @"^#\s*file:\s*(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : input.Name;
        var (ctl, rec) = input.Kind == "wps-pif" ? ("PCR", "PDR") : ("SCR", "EDR");
        var controls = lines.Where(x => x.l.StartsWith(ctl + ",")).ToList();
        var records = lines.Where(x => x.l.StartsWith(rec + ",")).ToList();
        if (controls.Count != 1) { f.Add(new("TB-F01", "file", $"One {ctl} control record, not {controls.Count}")); return f; }
        if (records.Count == 0) f.Add(new("TB-F02", "file", $"No {rec} record"));
        foreach (var x in lines.Where(x => !x.l.StartsWith(ctl + ",") && !x.l.StartsWith(rec + ","))) f.Add(new("TB-F03", $"line {x.n}", $"Not a {ctl} or {rec} record"));
        var c = controls[0].l.Split(',');
        var cw = $"line {controls[0].n}";
        if (c.Length < 10) { f.Add(new("TB-F04", cw, $"{ctl} has {c.Length} fields, not at least 10")); return f; }
        int count; decimal total;
        string currency;
        if (input.Kind == "wps-sif")
        {
            if (!Digits13.IsMatch(c[1])) f.Add(new("TB-F05", cw, "Employer id is not thirteen digits"));
            if (!Digits9.IsMatch(c[2])) f.Add(new("TB-F06", cw, "Employer bank routing code is not nine digits"));
            if (!Month.IsMatch(c[5])) f.Add(new("TB-F07", cw, "Salary month is not MMYYYY"));
            count = int.TryParse(c[6], out var n) ? n : -1; total = decimal.TryParse(c[7], NumberStyles.Number, CultureInfo.InvariantCulture, out var t) ? t : -1; currency = c[8];
            if (!fileName.StartsWith(c[1], StringComparison.Ordinal)) f.Add(new("TB-F08", "file name", "The file name does not start with the employer id"));
        }
        else
        {
            if (!Digits9.IsMatch(c[1])) f.Add(new("TB-F06", cw, "Agent routing code is not nine digits"));
            if (tenantRouting is not null && c[1] != tenantRouting) f.Add(new("TB-F09", cw, $"The file is addressed to {c[1]}, not to this bank ({tenantRouting})"));
            if (!Month.IsMatch(c[6])) f.Add(new("TB-F07", cw, "Salary month is not MMYYYY"));
            count = int.TryParse(c[7], out var n) ? n : -1; total = decimal.TryParse(c[8], NumberStyles.Number, CultureInfo.InvariantCulture, out var t) ? t : -1; currency = c[9];
        }
        if (currency != "AED") f.Add(new("TB-F11", cw, $"Currency is {currency}, not AED"));
        decimal sum = 0;
        var persons = new HashSet<string>();
        foreach (var (l, nn) in records)
        {
            var r = l.Split(',');
            var w = $"line {nn}";
            if (r.Length < 10) { f.Add(new("TB-F04", w, $"{rec} has {r.Length} fields, not 10")); continue; }
            if (!Digits14.IsMatch(r[1])) f.Add(new("TB-F12", w, "Person id is not fourteen digits"));
            if (!persons.Add(r[1])) f.Add(new("TB-F13", w, "Person id repeats an earlier record's"));
            if (!DataBuilder.IbanValid(r[3])) f.Add(new("TB-F10", w, "IBAN check digits are wrong"));
            if (DateOnly.TryParse(r[4], CultureInfo.InvariantCulture, out var s) && DateOnly.TryParse(r[5], CultureInfo.InvariantCulture, out var e) && s > e) f.Add(new("TB-F14", w, "Pay period ends before it starts"));
            sum += (decimal.TryParse(r[7], NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : 0) + (decimal.TryParse(r[8], NumberStyles.Number, CultureInfo.InvariantCulture, out var b) ? b : 0);
        }
        if (count != records.Count) f.Add(new("TB-F15", cw, $"Record count is {count}, the file has {records.Count}"));
        if (total != sum) f.Add(new("TB-F16", cw, $"Control total is {total.ToString(CultureInfo.InvariantCulture)}, the records sum to {sum.ToString(CultureInfo.InvariantCulture)}"));
        return f;
    }

    static IEnumerable<(string, string)> Ibans(JsonNode? n, string at = "context")
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var kv in o)
                    if (kv.Key == "iban" && kv.Value is JsonValue v) yield return (v.ToString(), $"{at}.iban");
                    else foreach (var x in Ibans(kv.Value, $"{at}.{kv.Key}")) yield return x;
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) foreach (var x in Ibans(a[i], $"{at}[{i}]")) yield return x;
                break;
        }
    }
}

/// <summary>
/// The check that refuses data that looks real before it is saved (runtime/testbench/synthetic.yaml). It is simple on
/// purpose and has limits (runtime/testbench/README.md): it knows IBANs, Emirates ID numbers, e-mail addresses, phone
/// numbers and a deny list of names, and it cannot tell a real person from a made-up one by name alone.
/// </summary>
public sealed class Synthetic(Home home)
{
    static readonly Regex IbanPattern = new(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b");
    static readonly Regex EmiratesId = new(@"\b784-?\d{4}-?\d{7}-?\d\b");
    static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})");
    static readonly Regex Phone = new(@"(\+971|00971)[\s-]?\d[\d\s-]{6,}");
    static readonly Regex XmlName = new(@"<Nm>([^<]*)</Nm>");

    public List<Finding> Check(string text)
    {
        var cfg = Yaml.Load(Path.Combine(home.Bench, "synthetic.yaml"));
        var codes = J.Strings(cfg["reservedBankCodes"]).ToHashSet();
        var allowed = J.Strings(cfg["allowedIbans"]).ToHashSet();
        var prefix = J.S(cfg, "namePrefix") ?? DataBuilder.SyntheticNamePrefix;
        var deny = J.Strings(cfg["denyNames"]).Select(d => d.ToLowerInvariant()).ToList();
        var emailDomains = J.Strings(cfg["emailDomains"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var f = new List<Finding>();
        foreach (Match m in IbanPattern.Matches(text))
        {
            var iban = m.Value;
            if (!DataBuilder.IbanValid(iban) || allowed.Contains(iban)) continue;
            if (iban.StartsWith("AE", StringComparison.Ordinal) && iban.Length == 23 && codes.Contains(iban[4..7])) continue;
            f.Add(new("TB-S01", iban[..4] + "…", "A valid IBAN outside the reserved test bank codes: it may be a real account"));
        }
        foreach (Match m in EmiratesId.Matches(text)) f.Add(new("TB-S02", "784-…", "An Emirates ID number"));
        foreach (Match m in Email.Matches(text)) if (!emailDomains.Contains(m.Groups[1].Value)) f.Add(new("TB-S03", "e-mail", $"An e-mail address outside {string.Join(", ", emailDomains)}"));
        foreach (Match m in Phone.Matches(text)) f.Add(new("TB-S04", "phone", "A UAE phone number"));
        foreach (Match m in XmlName.Matches(text))
        {
            var name = m.Groups[1].Value.Trim();
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) f.Add(new("TB-S05", "name", $"A party name that does not start with \"{prefix}\": synthetic names do"));
        }
        var lower = text.ToLowerInvariant();
        foreach (var d in deny) if (d.Length > 0 && lower.Contains(d)) f.Add(new("TB-S06", "deny list", "A name on the deny list"));
        return f;
    }
}
