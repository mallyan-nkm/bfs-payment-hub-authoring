using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Security;

namespace TransactHub.Security.Tests;

// The signature of a released package (spec R-09-23 to R-09-25), on its own: every key is made for the test and
// never written beyond the test's own folder.

public sealed class SigningTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);
    static readonly ReleaseClaims Claims = new("p.wf.corporate.v1/temporal@1.0.0", "p.wf.corporate.v1", "1.0.0", "temporal", "example-bank", "CS-2026-11-22-001");
    static readonly ReleaseExpectation Expected = new(Claims.ReleaseId, Claims.Tenant);

    static Dictionary<string, byte[]> Files() => new(StringComparer.Ordinal)
    {
        ["manifest.yaml"] = "kind: export\n"u8.ToArray(),
        ["README.md"] = "# Export package\n"u8.ToArray(),
        ["workflows/p.wf.corporate.v1@1.0.0.yaml"] = "kind: workflow\n"u8.ToArray(),
    };

    static Dictionary<string, byte[]> Signed(ECDsa key, Dictionary<string, byte[]>? files = null, ReleaseClaims? claims = null, DateTimeOffset? at = null)
    {
        files ??= Files();
        var (st, sig) = ReleaseSignature.Sign(files, claims ?? Claims, key, at ?? T0);
        return new Dictionary<string, byte[]>(files, StringComparer.Ordinal) { [ReleaseSignature.StatementFile] = st, [ReleaseSignature.SignatureFile] = sig };
    }

    static JsonObject Key(ECDsa k, DateTimeOffset? retiredAt = null)
    {
        var o = new JsonObject { ["keyId"] = ReleaseSignature.KeyIdOf(k), ["publicKey"] = k.ExportSubjectPublicKeyInfoPem() };
        if (retiredAt is { } r) o["retiredAt"] = r.ToString("O");
        return o;
    }

    static TrustedSigners Trust(IEnumerable<JsonObject> keys, IEnumerable<string>? revoked = null) => TrustedSigners.Parse(new JsonObject
    {
        ["kind"] = "trusted-signers",
        ["keys"] = new JsonArray(keys.Select(k => (JsonNode)k).ToArray()),
        ["revoked"] = new JsonArray((revoked ?? []).Select(id => (JsonNode)new JsonObject { ["keyId"] = id, ["revokedAt"] = T0.ToString("O"), ["reason"] = "test" }).ToArray()),
    }.ToJsonString());

    static string Refusal(Dictionary<string, byte[]> files, TrustedSigners trust, ReleaseExpectation? expected = null) =>
        Assert.Throws<SignatureRejected>(() => ReleaseSignature.Verify(files, expected ?? Expected, trust)).Message;

    [Fact]
    public void A_signed_package_verifies_and_names_its_key_and_change_set()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var v = ReleaseSignature.Verify(Signed(k), Expected, Trust([Key(k)]));
        Assert.Equal(ReleaseSignature.KeyIdOf(k), v.KeyId);
        Assert.Equal("CS-2026-11-22-001", v.ChangeSet);
        Assert.Equal(3, v.Files);
        Assert.Equal(32, v.KeyId.Length);
    }

    [Fact]
    public void A_tampered_extra_or_missing_file_is_refused()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust([Key(k)]);
        var tampered = Signed(k);
        tampered["workflows/p.wf.corporate.v1@1.0.0.yaml"] = "kind: workflow\n# edited\n"u8.ToArray();
        Assert.Contains("does not match its hash in the signed statement", Refusal(tampered, trust));
        var extra = Signed(k);
        extra["scripts/smuggled.js"] = "x"u8.ToArray();
        Assert.Contains("scripts/smuggled.js is not in the signed statement", Refusal(extra, trust));
        var missing = Signed(k);
        missing.Remove("README.md");
        Assert.Contains("Signed but not in the package: README.md", Refusal(missing, trust));
        // The statement itself edited (a file's hash put right): the signature no longer verifies.
        var st = Signed(k);
        var json = JsonNode.Parse(st[ReleaseSignature.StatementFile])!.AsObject();
        json["tenant"] = "other-bank";
        st[ReleaseSignature.StatementFile] = Encoding.UTF8.GetBytes(ReleaseSignature.Canonical(json));
        Assert.Contains("does not verify", Refusal(st, trust));
    }

    [Fact]
    public void A_missing_signature_a_wrong_key_and_a_revoked_key_are_refused()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Contains("is not signed", Refusal(Files(), Trust([Key(k)])));
        var onlySig = Signed(k);
        onlySig.Remove(ReleaseSignature.SignatureFile);
        Assert.Contains("is not signed", Refusal(onlySig, Trust([Key(k)])));
        Assert.Contains("does not trust", Refusal(Signed(other), Trust([Key(k)])));
        Assert.Contains("was revoked", Refusal(Signed(k), Trust([Key(k)], [ReleaseSignature.KeyIdOf(k)])));
        // A signature made by another key, put under the trusted key's statement.
        var swapped = Signed(k);
        swapped[ReleaseSignature.SignatureFile] = Signed(other)[ReleaseSignature.SignatureFile];
        Assert.Contains("does not verify", Refusal(swapped, Trust([Key(k), Key(other)])));
    }

    [Fact]
    public void A_signature_for_another_release_or_tenant_is_refused()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust([Key(k)]);
        Assert.Contains("is for p.wf.corporate.v1/temporal@1.0.0, not p.wf.corporate.v1/temporal@1.0.1", Refusal(Signed(k), trust, Expected with { ReleaseId = "p.wf.corporate.v1/temporal@1.0.1" }));
        Assert.Contains("for tenant example-bank, not (shared)", Refusal(Signed(k), trust, Expected with { Tenant = null }));
    }

    [Fact]
    public void Keys_rotate_a_new_key_is_added_the_old_retires_and_still_verifies_what_it_signed_before()
    {
        using var old = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var retired = T0.AddDays(10);
        var trust = Trust([Key(old, retiredAt: retired), Key(next)]);
        // Before the switch: both keys verify; after it, only the new key signs new releases.
        Assert.Equal(ReleaseSignature.KeyIdOf(old), ReleaseSignature.Verify(Signed(old, at: T0), Expected, trust).KeyId);
        Assert.Equal(ReleaseSignature.KeyIdOf(next), ReleaseSignature.Verify(Signed(next, at: T0.AddDays(11)), Expected, trust).KeyId);
        Assert.Contains("which was retired", Refusal(Signed(old, at: T0.AddDays(11)), trust));
        // Revoking the old key after rotation: what it signed no longer verifies, what the new key signed still does.
        var revoked = Trust([Key(next)], [ReleaseSignature.KeyIdOf(old)]);
        Assert.Contains("was revoked", Refusal(Signed(old, at: T0), revoked));
        Assert.Equal(ReleaseSignature.KeyIdOf(next), ReleaseSignature.Verify(Signed(next, at: T0.AddDays(11)), Expected, revoked).KeyId);
    }

    [Fact]
    public void A_statement_says_its_kind_and_carries_a_kind_s_own_fields_under_the_signature()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = Trust([Key(k)]);
        // A distribution (docs/runtime/PACKAGING-AND-DEPLOYMENT.md): its bill of materials is signed with the files.
        var bom = new JsonObject { ["billOfMaterials"] = new JsonArray(new JsonObject { ["pack"] = "p", ["release"] = "1.4.0" }) };
        var dist = Claims with { ReleaseId = "dist/example-bank/2026.11.22-1", Kind = ReleaseSignature.Distribution, Extra = bom };
        var files = Signed(k, claims: dist);
        var v = ReleaseSignature.Verify(files, new ReleaseExpectation(dist.ReleaseId, dist.Tenant, ReleaseSignature.Distribution), trust);
        Assert.Equal(ReleaseSignature.Distribution, v.Kind);
        Assert.Equal("1.4.0", (string)JsonNode.Parse(files[ReleaseSignature.StatementFile])!["billOfMaterials"]![0]!["release"]!);
        // A distribution is not a workflow package, whatever its files.
        Assert.Contains("is for a distribution, not a workflow-package", Refusal(files, trust, new ReleaseExpectation(dist.ReleaseId, dist.Tenant)));
    }

    [Fact]
    public void A_trusted_signers_list_cannot_name_a_key_by_another_key_s_id()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var lie = Key(other);
        lie["keyId"] = ReleaseSignature.KeyIdOf(k);
        Assert.Contains("is not the id of its public key", Assert.Throws<SignatureRejected>(() => Trust([lie])).Message);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Contains("not on P-256", Assert.Throws<SignatureRejected>(() => Trust([Key(p384)])).Message);
    }

    [Fact]
    public void The_signature_checks_with_openssl_as_a_detached_blob_signature()
    {
        if (!Tool("openssl", "version")) return;
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dir = Directory.CreateTempSubdirectory("th-sig-").FullName;
        try
        {
            var s = Signed(k);
            File.WriteAllBytes(Path.Combine(dir, "release.json"), s[ReleaseSignature.StatementFile]);
            File.WriteAllBytes(Path.Combine(dir, "release.der"), Convert.FromBase64String(Encoding.ASCII.GetString(s[ReleaseSignature.SignatureFile]).Trim()));
            File.WriteAllText(Path.Combine(dir, "signer.pem"), k.ExportSubjectPublicKeyInfoPem());
            var p = Process.Start(new ProcessStartInfo("openssl", ["dgst", "-sha256", "-verify", "signer.pem", "-signature", "release.der", "release.json"]) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("Verified OK", output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static bool Tool(string name, string arg)
    {
        try { using var p = Process.Start(new ProcessStartInfo(name, arg) { RedirectStandardOutput = true, RedirectStandardError = true })!; p.WaitForExit(); return p.ExitCode == 0; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
