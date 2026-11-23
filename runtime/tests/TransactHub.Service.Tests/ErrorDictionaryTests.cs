using System.Text.RegularExpressions;
using TransactHub.Security;

namespace TransactHub.Service.Tests;

/// <summary>
/// The runtime's half of check L39 (R-05-51): every code the runtime raises has an entry in the platform's dictionary, as
/// published on the content branch: the codes of <see cref="ErrorCodes"/>, every <c>NativeFailure</c> and <c>StepFailure</c>
/// code in the runtime's and the interpreter's source, and every refusal a signature gives.
/// </summary>
public sealed class ErrorDictionaryTests
{
    static readonly ErrorDictionary Dict = ErrorDictionary.FromFolder(Path.Combine(Repo.Root(), "content-configurations", "artefacts"));

    [Fact]
    public void Every_runtime_code_has_an_entry_with_a_runbook()
    {
        var missing = ErrorCodes.All.Where(c => Dict.Find(c) is null).ToList();
        Assert.Empty(missing);
        foreach (var e in Dict.All)
        {
            Assert.NotEmpty(e.Doc["runbook"]!["checks"]!.AsArray());
            Assert.NotEmpty(e.Doc["runbook"]!["safeActions"]!.AsArray());
            foreach (var a in e.Actions) Assert.Contains(ActionBook.Catalogue, x => x.Id == a);
        }
    }

    [Fact]
    public void Every_code_a_native_activity_or_the_interpreter_raises_has_an_entry()
    {
        var root = Repo.Root();
        var files = Directory.EnumerateFiles(Path.Combine(root, "runtime", "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "studio", "engines", "shared"), "*.cs"))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"));
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(text, @"new NativeFailure\(""([A-Za-z.:-]+)""")) codes.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"new StepFailure\(.*,\s*""([A-Za-z]+)""\);\s*$", RegexOptions.Multiline)) codes.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"new AttemptFailed\(""([A-Za-z]+)""")) codes.Add(m.Groups[1].Value);
            // The native host's own refusals (Issue I-230), and the fallback of a module refusal with no code of its own.
            foreach (Match m in Regex.Matches(text, @"new NativeRefusal\((?:e\.Code \?\? )?""([A-Za-z.:-]+)""")) codes.Add(m.Groups[1].Value);
        }
        codes.Add("Failure");
        Assert.Contains("PortNotBound", codes);
        Assert.Contains("native.WrongDistribution", codes);
        Assert.Contains("native.NotPinned", codes);
        Assert.Contains("native.ModuleRefused", codes);
        Assert.Contains("RetriesExhausted", codes);
        Assert.DoesNotContain(codes, c => Dict.Find(c) is null);
    }

    [Fact]
    public void A_signatures_refusals_and_a_packages_have_their_own_codes()
    {
        // Each refusal carries its code where it is thrown (Issue I-103): the words are the message only.
        Assert.Equal(ErrorCodes.NotSigned, ErrorCodes.Of(new SignatureRejected("any words", SignatureRejected.NotSigned)));
        Assert.Equal(ErrorCodes.Tampered, ErrorCodes.Of(new SignatureRejected("The package is not signed (words only)")));
        Assert.Equal(ErrorCodes.PackageRejected, ErrorCodes.Of(new TransactHub.Api.PackageRejected("The package has no manifest.yaml")));
        Assert.Equal(ErrorCodes.MakerApproves, ErrorCodes.Of(new TransactHub.Operations.ActionRefused(403, "any words", TransactHub.Operations.OpsCodes.MakerApproves)));
        Assert.Equal(ErrorCodes.ReleaseConflict, ErrorCodes.Of(new TransactHub.Store.ReleaseConflict("x")));
        // The codes ReleaseSignature.Verify sets for each refusal it gives.
        var trust = TrustedSigners.Parse("{\"kind\":\"trusted-signers\",\"keys\":[]}");
        var unsigned = Assert.Throws<SignatureRejected>(() => ReleaseSignature.Verify(new Dictionary<string, byte[]> { ["a.yaml"] = [1] }, new ReleaseExpectation("r@1.0.0", null), trust));
        Assert.Equal(ErrorCodes.NotSigned, ErrorCodes.Of(unsigned));
        // A scheme's code is looked up in its own scope first, then the cluster's, then the platform's.
        Assert.Equal("uae.wps", Dict.Find("FF01", "uae.wps")!.Scope);
        Assert.Equal("uae.p", Dict.Find("RC01", "uae.wps")!.Scope);
        Assert.Equal("p", Dict.Find("AM02", "uae.wps")!.Scope);
        Assert.Null(Dict.Find("FF01", "p"));
        Assert.Equal(["AM02", "TransientError"], Dict.FoundIn("step failed: TransientError after AM02").Select(e => e.Code).Order());
    }
}
