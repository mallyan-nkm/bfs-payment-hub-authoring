// The golden corpus of the WPS salary-file rules (plan section 4: "no functionality is lost").
// This one file is compiled twice: with SOURCE defined, against the TransactHub source's own rule classes,
// to write golden.json; without, against the runtime's port, to check that every case gives the same
// answer. The inputs are synthetic and generated here, deterministically.
#if SOURCE
using PaymentHub.Activities.Platform.Wps;
using PaymentHub.Shared.Domain.Wps;
using PaymentHub.Shared.Security;
using Failure = PaymentHub.Shared.Domain.Wps.SifUploadRuleFailure;
#else
using TransactHub.Activities.Wps.Rules;
using TransactHub.Activities.Wps.Sif;
using Failure = TransactHub.Activities.Wps.Rules.WpsRuleFailure;
#endif
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TransactHub.WpsGolden;

public static class Corpus
{
    static string F(Failure? f) => f is null ? "ok" : $"{f.ErrorCode}|{f.Message}";
    static string L(IReadOnlyList<string> xs) => xs.Count == 0 ? "ok" : string.Join("\n", xs);
    static string Q(object? x) => JsonSerializer.Serialize(x);

    /// <summary>The Central Bank SIF the source generates from stored records: content, hash, totals, or why not.</summary>
    static void SifGeneration(Action<string, object, string> add, Func<Func<string>, string> guard)
    {
        var created = new DateTime(2026, 11, 17, 9, 5, 0);
        CbxSifEmployee E(long id, int order, string person, decimal fixedPay, decimal variablePay, string agent = "203320101", string account = "AE070331234567890123456", int days = 31, decimal leave = 0) =>
            new(id, order, person, agent, account, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), days, fixedPay, variablePay, leave);
        CbxSifVariablePay V(long id, string person, decimal housing, decimal other = 0, string routing = "203320101") =>
            new(id, person, routing, housing, 10m, 0m, 0m, 0.005m, other, 0m);
        var control = new CbxSifControl("1234567", "302620122", created, "102026", "AED", "CORP-0001");
        var employees = new List<CbxSifEmployee> { E(10, 2, "1234567890", 1200m, 300.125m), E(11, 1, "00001234567891", 800.5m, 0m), E(12, 2, "1234567892", 100m, 0.004m) };
        var pays = new List<CbxSifVariablePay> { V(5, "1234567890", 200m, 90.125m), V(6, "00001234567890", 1m), V(7, "1234567892", 0m) };

        var variants = new List<(string what, List<CbxSifEmployee> e, List<CbxSifVariablePay> v, CbxSifControl c)>
        {
            ("valid", employees, pays, control),
            ("no employees", new(), pays, control),
            ("no variable pay", employees, new(), control),
            ("one employee", employees.Take(1).ToList(), pays.Take(1).ToList(), control),
            ("evp for nobody", employees, [.. pays, V(8, "99999999999999", 1m)], control),
            ("comma in account", [.. employees, E(13, 3, "1234567893", 1m, 0m, account: "AE07,1")], pays, control),
            ("line break in agent", [.. employees, E(13, 3, "1234567893", 1m, 0m, agent: "2033\n20101")], pays, control),
            ("non-ascii person", [.. employees, E(13, 3, "١٢٣٤", 1m, 0m)], pays, control),
            ("blank person", [.. employees, E(13, 3, " ", 1m, 0m)], pays, control),
            ("negative fixed", [.. employees, E(13, 3, "1234567893", -1m, 0m)], pays, control),
            ("negative days", [.. employees, E(13, 3, "1234567893", 1m, 0m, days: -1)], pays, control),
            ("negative leave", [.. employees, E(13, 3, "1234567893", 1m, 0m, leave: -0.5m)], pays, control),
            ("negative evp", employees, [.. pays, V(9, "1234567890", -2m)], control),
            ("long person id", [.. employees, E(13, 3, "123456789012345678", 1m, 0m)], pays, control),
            ("rounding", [E(1, 1, "1", 0.005m, 0.015m), E(2, 2, "2", 2.675m, 1.005m), E(3, 3, "3", 999999999999.995m, 0m)], new(), control),
        };
        foreach (var (field, c) in new (string, CbxSifControl)[]
        {
            ("month 13", control with { SalaryMonthYear = "132026" }), ("month 00", control with { SalaryMonthYear = "002026" }),
            ("month short", control with { SalaryMonthYear = "10202" }), ("month letters", control with { SalaryMonthYear = "1O2026" }),
            ("blank currency", control with { Currency = " " }), ("comma reference", control with { EmployerReference = "A,B" }),
            ("blank routing", control with { BankRoutingCode = "" }), ("padded values", control with { BankRoutingCode = " 302620122 ", Currency = " AED ", SalaryMonthYear = " 102026 " }),
            ("long establishment", control with { EstablishmentId = "12345678901234" }), ("midnight", control with { CreatedAt = new DateTime(2026, 11, 17, 0, 0, 59) }),
        })
            variants.Add((field, employees, pays, c));

        foreach (var (what, e, v, c) in variants)
            add("CbxSifWriter.Build", new { what }, guard(() =>
            {
                var (doc, failure) = CbxSifWriter.Build(e, v, c);
                return doc is null ? $"{failure!.ErrorCode}|{failure.Message}" : Q(new { doc.Content, doc.Sha256, doc.EdrCount, doc.EvpCount, doc.TotalAmount });
            }));
        foreach (var n in new[] { "0000001234567261117083000.SIF", "0000001234567261117083000.sif", "0000001234567261117083000.SIF.GPG", "0000001234567261117083000.sif.gpg", ".SIF", "x.GPG", "x.txt", " " })
            add("CbxSifWriter.FileNameFor", new { n }, guard(() => CbxSifWriter.FileNameFor(n)));
        foreach (var m in new[] { "0000001234567", " 0000001234567 ", "ABC-1", "" })
            add("CbxSifWriter.FallbackFileName", new { m }, guard(() => CbxSifWriter.FallbackFileName(m, created)));
        add("CbxSifWriter.Encode", new { s = "EDR,1\r\n" }, Convert.ToHexString(CbxSifWriter.Encode("EDR,1\r\n")));
    }

    /// <summary>The Central Bank's acknowledgement (ACK/NAK): its name, its records, what is wrong with it; and the own/other bank split.</summary>
    static void AckNak(Action<string, object, string> add, Func<Func<string>, string> guard)
    {
        var names = new[] { "0000001234567261117083000S26000000001.ACK", "0000001234567261117083000S26000000001.NAK", "/in/0000001234567261117083000S26000000001.ack",
                            "C:\\in\\0000001234567261117083000S26000000001", "S26000000001", "S2600000000", "x.ACK", "", "00000012345672611170830001.ACK" };
        foreach (var n in names)
        {
            add("WpsAckNakFile.TrySplitFileName", new { n }, guard(() => WpsAckNakFile.TrySplitFileName(n, out var p, out var id) ? Q(new { p, id }) : "false"));
            add("WpsAckNakFile.TryDeriveSifFileName", new { n }, guard(() => WpsAckNakFile.TryDeriveSifFileName(n, out var s) ? s : "false"));
        }
        foreach (var (sif, id, ok) in new[] { ("0000001234567261117083000.SIF", "S26000000001", true), ("0000001234567261117083000.SIF", "S26000000001", false), ("0000001234567261117083000", "S26000000001", true), ("x.sif", "S2", true), ("", "", true) })
            add("WpsAckNakFile.BuildFileName", new { sif, id, ok }, guard(() => WpsAckNakFile.BuildFileName(sif, id, ok)));
        foreach (var (c, at, seq) in new[] { ('S', new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc), 1), ('A', new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 999999999), ('S', new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1234567890), ('s', new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), -1) })
            add("WpsAckNakFile.GenerateWpsFileId", new { c = c.ToString(), at, seq }, guard(() => WpsAckNakFile.GenerateWpsFileId(c, at, seq)));
        var der = new[] { new WpsAckDetailError(3, "00803", "Last record is not SCR"), new WpsAckDetailError(0, "00829", "More than one SCR, see line 9, 10") };
        var contents = new[]
        {
            WpsAckNakFile.Build("0000001234567261117083000.SIF", true),
            WpsAckNakFile.Build("0000001234567261117083000.SIF", false, der),
            WpsAckNakFile.Build("0000001234567261117083000.SIF", true, der),
            "AHR,ACCEPTED,0000001234567261117083000.SIF\r\nATR,ACCEPTED,2\r\n",
            "ahr, accepted ,a,b.SIF\nATR,ACCEPTED,2\n",
            "AHR,REJECTED,f.SIF\nDER,x,00803,desc, with comma\nDER,1\nATR,REJECTED,4\n",
            "AHR,ACCEPTED,f.SIF\nATR,REJECTED,2\n",
            "AHR,ACCEPTED,f.SIF\nATR,ACCEPTED,9\n",
            "AHR,ACCEPTED,f.SIF\n",
            "ATR,ACCEPTED,1\n",
            "AHR,ACCEPTED\nATR\n",
            "\n\n  \nXYZ,1\nAHR,ACCEPTED,f.SIF\nATR,ACCEPTED,x\n",
            "AHR,ACCEPTED,f.SIF\rATR,ACCEPTED,2\r",
            "",
        };
        foreach (var c in contents)
            foreach (var src in new string?[] { null, "0000001234567261117083000S26000000001.ACK", "short" })
                add("WpsAckNakFile.Parse", new { c, src }, guard(() =>
                {
                    var d = WpsAckNakFile.Parse(c, src);
                    return Q(new { d.Status, d.ProcessedFileName, Details = d.DetailErrors.Select(e => $"{e.LineNumber}|{e.ErrorNumber}|{e.ErrorDescription}").ToList(), d.TrailerStatus, d.TrailerLineCount, d.ActualLineCount, d.SourceFileName, d.WpsFileId, d.DerivedSifFileName, d.ParseErrors });
                }));
        foreach (var c in contents.Take(3)) add("WpsAckNakFile.Build", new { c }, c);
        foreach (var e in new[] { "302620122", " 302620122 ", "302620123", "30262012", "", null })
            foreach (var conf in new[] { "302620122", " 302620122", "" })
                add("WpsEmployeeBankClassifier.Classify", new { e, conf }, guard(() => WpsEmployeeBankClassifier.Classify(e, conf).ToString()));
        foreach (var r in new[] { "302620122", "30262012", "3026201220", "30262012X", " 302620122", "", null, "000000000", "١٢٣٤٥٦٧٨٩" })
            add("WpsRoutingCode.IsValid", new { r }, guard(() => WpsRoutingCode.IsValid(r, out var err) ? "valid" : $"invalid|{err}"));
    }

    static CbxRuleSettings Settings(int mol, int years, bool req, bool reg, int match) => new(mol, years, req, reg, match);

    /// <summary>Every case, in a fixed order: the rule, its inputs as text, and its answer as text.</summary>
    public static JsonArray Run()
    {
        var cases = new JsonArray();
        void add(string rule, object input, string result) =>
            cases.Add(new JsonObject { ["rule"] = rule, ["input"] = Q(input), ["result"] = result });
        string guard(Func<string> f) { try { return f(); } catch (Exception e) { return $"throws {e.GetType().Name}"; } }

        var mols = new[] { "0000001234567", "00000012345678", "ABCDEF1234567", "000000123456!", "０００0001234567" };
        var stamps = new[] { "261117083000", "251231235959", "261117083001", "2611170830", "26111708300X" };
        var suffixes = new[] { ".SIF", ".sif", ".SIF.GPG", ".sif.gpg", ".GPG", ".TXT", ".SIF.GPG.SIF", "" };
        var names = new List<string>();
        foreach (var m in mols) foreach (var t in stamps) foreach (var x in suffixes) names.Add(m + t + x);
        names.AddRange(new[] { "", "../0000001234567261117083000.SIF", "0000001234567261117083000.S IF", "a..b.SIF", new string('1', 260) + ".SIF", "0000001234567/61117083000.SIF", "C:0000001234567261117083000.SIF" });

        // CBX file-name rules 1–3, for MOL id lengths 13 and 14.
        foreach (var mol in new[] { 13, 14 })
            foreach (var n in names)
                add("CbxFileNameRules.Check", new { n, mol }, guard(() =>
                {
                    var (name, failure) = CbxFileNameRules.Check(n, Settings(mol, 1, true, true, 15));
                    return name is null ? F(failure) : Q(new { name.MolEstablishmentId, name.DateComponent, name.IsEncrypted, name.SifFileName });
                }));
        foreach (var mol in new[] { 13, 35 })
            foreach (var enc in new[] { false, true })
                add("CbxFileNameRules.ExpectedLength", new { mol, enc }, CbxFileNameRules.ExpectedLength(Settings(mol, 1, true, true, 15), enc).ToString());

        // Rules 5–7 on the timestamp, around a fixed business time.
        var now = new DateTime(2026, 11, 17, 8, 30, 0);
        var dates = new[] { "261117083000", "261117082959", "261117083001", "261117083459", "261117083500", "251117083000", "241117083000", "991231235959",
                            "261317083000", "260230120000", "250229120000", "240229120000", "2611170830", "26111708300X", "", "261117243000", "261117086000", "000101000000" };
        foreach (var years in new[] { 0, 1, 2 })
            foreach (var skewMin in new[] { 0, 5 })
                foreach (var d in dates)
                    add("CbxFileNameRules.CheckFileNameDate", new { d, years, skewMin }, guard(() => F(CbxFileNameRules.CheckFileNameDate(d, now, TimeSpan.FromMinutes(skewMin), Settings(13, years, true, true, 15)))));

        // EDR lines: person id, agent routing code, account, pay fields; mainland and freezone corporates.
        var ids = new[] { "00001234567890", "12345", "1,234", "ABC123", "FZ1000000000000001", "FZ10", "fz1000000000000001", "" };
        var agents = new[] { "203320101", "200220101", "205720101", "205820101", "200120101", "20A320101", "203", "" };
        var accounts = new[] { "AE070331234567890123456", "AE070321234567890123456", "ae070331234567890123456", "AE07033", "AE0703", "1234567890", "" };
        var pays = new[] { "1200.00", "123456789012345", "1234567890123456", " 300 " };
        var zones = new[] { null, "", "000", " 000 ", "FZ1", "FZ" };
        var line = 0;
        foreach (var id in ids) foreach (var ag in agents) foreach (var ac in accounts)
        {
            var pay = pays[line % pays.Length];
            var zone = zones[line % zones.Length];
            var fields = new List<string> { "EDR", id, ag, ac, "2026-10-01", "2026-10-31", "31", pay, pays[(line + 1) % pays.Length], "0" };
            add("CbxRecordRules.CheckEdr", new { fields, line, zone }, guard(() => L(CbxRecordRules.CheckEdr(fields, ++line, zone))));
        }
        add("CbxRecordRules.CheckEdr", new { fields = new[] { "EDR", "1" }, line = 1, zone = (string?)null }, L(CbxRecordRules.CheckEdr(new[] { "EDR", "1" }, 1, null)));

        // EVP lines: employee id and the seven allowances.
        var allowances = new[] { "0", "", "123456789012345", "1234567890123456", " 5 " };
        line = 0;
        foreach (var id in ids) foreach (var zone in zones)
        {
            var fields = new List<string> { "EVP", id, "x" };
            for (var i = 0; i < 7; i++) fields.Add(allowances[(line + i) % allowances.Length]);
            add("CbxRecordRules.CheckEvp", new { fields, line, zone }, guard(() => L(CbxRecordRules.CheckEvp(fields, ++line, zone))));
        }
        foreach (var zone in zones) add("CbxRecordRules.IsFreezone", new { zone }, CbxRecordRules.IsFreezone(zone).ToString());
        foreach (var ac in accounts) add("CbxRecordRules.DuplicateAgentAccountMessage", new { ac }, CbxRecordRules.DuplicateAgentAccountMessage(7, ac, "203320101"));

        // Rule 79: an empty line before the control record.
        var layouts = new[]
        {
            new[] { "EDR,1", "EDR,2", "SCR,x" }, new[] { "EDR,1", "", "SCR,x" }, new[] { "", "EDR,1", "SCR,x" }, new[] { "EDR,1", "  ", "SCR,x" },
            new[] { "EDR,1", "SCR,x", "" }, new[] { "EDR,1", "EDR,2" }, new[] { "EDR,1", "", "EDR,2" }, new[] { "  SCR,x", "" }, Array.Empty<string>(),
        };
        foreach (var lines in layouts) add("CbxRecordRules.CheckEmptyLineBeforeScr", new { lines }, CbxRecordRules.CheckEmptyLineBeforeScr(lines) ?? "ok");

        // The control record: rule 66 (the bank's own routing code), 81 (field count), 82 and 83 (the debit account).
        var scrs = new[]
        {
            new[] { "SCR", "0000001234567", "302620122", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "000000000000001" },
            new[] { "SCR", "0000001234567", " 302620122 ", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "AE070300000000000000001" },
            new[] { "SCR", "0000001234567", "302620123", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "" },
            new[] { "SCR", "0000001234567", "302620122", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "  " },
            new[] { "SCR", "0000001234567", "302620122", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "0000-00000001" },
            new[] { "SCR", "0000001234567", "302620122", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", new string('1', 35) },
            new[] { "SCR", "0000001234567", "302620122", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "999000000000001" },
            new[] { "SCR", "0000001234567", "302620122", "2026-11-17", "0830", "102026", "1", "1500.00", "AED", "111000000000009" },
            new[] { "SCR", "0000001234567" },
            Array.Empty<string>(),
        };
        var registered = new[]
        {
            new string?[] { "000000000000001" },
            new string?[] { "AE070300000000000000001", "000000000000002" },
            new string?[] { "XX0000000000000000001", "YY0000000000000000001" },
            new string?[] { null, "", "  ", "0000-00000001" },
            new string?[] { "111000000000009", "111000000000009" },
            Array.Empty<string?>(),
        };
        var lineNo = 0;
        foreach (var scr in scrs)
        {
            lineNo++;
            add("CbxScrRules.CheckBankRoutingCode", new { scr }, F(CbxScrRules.CheckBankRoutingCode(scr, "302620122", lineNo)));
            add("CbxScrRules.ReadDebitAccount", new { scr }, CbxScrRules.ReadDebitAccount(scr) ?? "null");
            foreach (var reg in registered)
                foreach (var (req, chk, match) in new[] { (true, true, 15), (false, true, 15), (true, false, 15), (false, false, 15), (true, true, 23), (true, true, 1) })
                    add("CbxScrRules.CheckDebitAccount", new { scr, reg, req, chk, match }, guard(() =>
                    {
                        var r = CbxScrRules.CheckDebitAccount(scr, reg, Settings(13, 1, req, chk, match));
                        return Q(new { r.DebitAccount, r.RegisteredAccount, failure = F(r.Failure) });
                    }));
        }
        add("CbxScrRules.ColumnCountMessage", new { line = 9 }, CbxScrRules.ColumnCountMessage(9));

        // The bank's values for the rules: missing, malformed or out of range is refused by name.
        var good = new Dictionary<string, string?> { ["CbxMolIdLength"] = "13", ["CbxFileDateMaxYearsBack"] = "1", ["CbxScrDebitAccountRequired"] = "true", ["CbxScrDebitAccountRegisteredCheck"] = "False", ["CbxAccountMatchLength"] = "15" };
        var variants = new List<Dictionary<string, string?>> { good };
        foreach (var k in good.Keys)
            foreach (var v in new string?[] { null, "", " 14 ", "-1", "abc", "0", "99", "100", "TRUE", "yes", "36", "35", "34" })
            {
                var d = new Dictionary<string, string?>(good);
                if (v is null) d.Remove(k); else d[k] = v;
                variants.Add(d);
            }
        foreach (var d in variants)
            add("CbxRuleSettings.FromParameters", new { d = d.OrderBy(x => x.Key).ToList() }, guard(() =>
            {
                var (s, bad) = CbxRuleSettings.FromParameters(d);
                return s is null ? $"invalid {bad}" : Q(new { s.MolIdLength, s.FileDateMaxYearsBack, s.ScrDebitAccountRequired, s.ScrDebitAccountRegisteredCheck, s.AccountMatchLength });
            }));
        foreach (var (mol, years, match) in new[] { (12, 1, 15), (36, 1, 15), (13, -1, 15), (13, 100, 15), (13, 1, 0), (13, 1, 35) })
            add("CbxRuleSettings.new", new { mol, years, match }, guard(() => Settings(mol, years, true, true, match).MolIdLength.ToString()));

        // The pre-checks every channel applies.
        foreach (var n in names.Take(120).Concat(new[] { "0000001234567261117083000.GPG", new string('1', 35) + "261117083000.SIF", new string('1', 36) + "261117083000.SIF" }))
        {
            add("SifUploadRules.ParseFileName", new { n }, guard(() => { var (name, f) = SifUploadRules.ParseFileName(n); return name is null ? F(f) : Q(new { name.MolEstablishmentId, name.DateComponent }); }));
            add("SifUploadRules.FileNameVariants", new { n }, guard(() => Q(SifUploadRules.FileNameVariants(n))));
        }
        foreach (var d in dates)
            foreach (var skewMin in new[] { 0, 5 })
                add("SifUploadRules.CheckFileNameDate", new { d, skewMin }, guard(() => F(SifUploadRules.CheckFileNameDate(d, now, TimeSpan.FromMinutes(skewMin), "Asia/Dubai"))));
        var corp = new SifRegisteredCorporate("C1", "CIF0001", "000000000000001", "payroll@example.com", "C");
        foreach (var mol in new[] { "0000001234567", "", "  " })
            foreach (var c in new[] { corp, null })
                foreach (var (cif, acc, mail) in new (string?, string?, string?)[] { (null, null, null), ("cif0001", null, null), ("CIF9", null, null), (null, " 000000000000001 ", null), (null, "000000000000009", null), (null, null, "PAYROLL@example.com"), (null, null, "other@example.com"), ("", "", "") })
                    add("SifUploadRules.MatchCorporate", new { mol, corporate = c is not null, cif, acc, mail }, F(SifUploadRules.MatchCorporate(mol, c, cif, acc, mail)));

        SifGeneration(add, guard);
        AckNak(add, guard);

        foreach (var a in new[] { null, "", "  ", "123456", "1234567", "AE070331234567890123456" })
            foreach (var k in new[] { 6, 4 })
                add("AccountMasking.Mask", new { a, k }, AccountMasking.Mask(a, k));
        return cases;
    }
}
