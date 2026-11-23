using System.Text;
using TransactHub.Ports;
using TransactHub.Security;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace TransactHub.Store.Tests;

// File encryption (R-03-24, R-12-13; Issue I-87): the openpgp adapters of p.port.encryption.v1 and p.port.decryption.v1, and
// the two bank folders that encrypt their salary files for the Central Bank (DIB and ADIB). Every key is made by the test
// when it runs; none is in the repository, and every key a binding names is a reference.

public sealed class EncryptionTests
{
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-22T00:00:00Z");
    // A synthetic salary file: invented ids and amounts.
    static readonly byte[] Sif = Encoding.UTF8.GetBytes("SCR,0000001234567,302620122,2026-11-17,0830,102026,1,1500.00,AED,REF-0001\nEDR,00001234567890,203320101,AE070331234567890123456,2026-10-01,2026-10-31,31,1200.00,300.00,0\n");

    sealed record Keys(byte[] Public, byte[] Secret, string Passphrase);

    static Keys NewKeys(string identity = "Central Bank WPS (test)")
    {
        var pass = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var (pub, sec) = OpenPgp.GenerateKeyPair(identity, pass.ToCharArray());
        return new Keys(pub, sec, pass);
    }

    static (IFileEncryption enc, IFileDecryption dec) Adapters(Keys recipient, bool armor = false)
    {
        var env = new Dictionary<string, string>
        {
            ["TH_TEST_CB_WPS_PUBLIC_KEY"] = Encoding.UTF8.GetString(recipient.Public),
            ["TH_TEST_CB_WPS_SECRET_KEY"] = Encoding.UTF8.GetString(recipient.Secret),
            ["TH_TEST_CB_WPS_PASSPHRASE"] = recipient.Passphrase,
        };
        var secrets = new SecretResolver([new EnvSecretProvider(k => env.GetValueOrDefault(k))]);
        var e = new PortBinding("test-bank", OpenPgpEncryption.Port, OpenPgpEncryption.Name, From, new Dictionary<string, string>
        {
            ["keys.cbuae-wps.secretRef"] = "env:TH_TEST_CB_WPS_PUBLIC_KEY", ["armor"] = armor ? "true" : "false",
        });
        // The recipient's side: what the Central Bank would hold, here only to prove the round trip.
        var d = new PortBinding("test-bank", OpenPgpDecryption.Port, OpenPgpDecryption.Name, From, new Dictionary<string, string>
        {
            ["privateKeyRef"] = "env:TH_TEST_CB_WPS_SECRET_KEY", ["secretRef"] = "env:TH_TEST_CB_WPS_PASSPHRASE",
        });
        return (FileProtectionAdapters.Encryption(e, secrets), FileProtectionAdapters.Decryption(d, secrets));
    }

    [Fact]
    public async Task A_file_encrypted_for_the_recipient_decrypts_with_its_private_key_and_only_that()
    {
        var keys = NewKeys();
        var (enc, dec) = Adapters(keys);
        var cipher = await enc.Encrypt(Sif, "cbuae-wps", "0000001234567261117083000.sif");
        Assert.True(OpenPgp.IsEncrypted(cipher));
        Assert.False(OpenPgp.IsEncrypted(Sif));
        Assert.DoesNotContain("EDR,", Encoding.Latin1.GetString(cipher));
        Assert.Equal(Sif, await dec.Decrypt(cipher));
        // Encrypting the same file again gives other bytes (a new session key), and the same file back.
        var again = await enc.Encrypt(Sif, "cbuae-wps", "0000001234567261117083000.sif");
        Assert.NotEqual(cipher, again);
        Assert.Equal(Sif, await dec.Decrypt(again));
        // Another key's holder cannot open it.
        var (_, other) = Adapters(NewKeys("Someone else (test)"));
        var e = await Assert.ThrowsAsync<FileProtectionException>(() => other.Decrypt(cipher));
        Assert.Contains("no key of this binding", e.Message);
    }

    [Fact]
    public async Task Armoured_output_when_the_binding_asks_for_it_and_a_tampered_file_fails_its_integrity_check()
    {
        var keys = NewKeys();
        var (enc, dec) = Adapters(keys, armor: true);
        var cipher = await enc.Encrypt(Sif, "cbuae-wps", "f.sif");
        Assert.StartsWith("-----BEGIN PGP MESSAGE-----", Encoding.ASCII.GetString(cipher));
        Assert.Equal(Sif, await dec.Decrypt(cipher));
        var (binEnc, binDec) = Adapters(keys);
        var bin = await binEnc.Encrypt(Sif, "cbuae-wps", "f.sif");
        bin[^20] ^= 0x5A;
        await Assert.ThrowsAnyAsync<Exception>(() => binDec.Decrypt(bin));
    }

    [Fact]
    public async Task A_key_id_the_binding_does_not_name_and_a_key_that_is_not_a_reference_are_refused_naming_no_key()
    {
        var keys = NewKeys();
        var (enc, _) = Adapters(keys);
        var e = await Assert.ThrowsAsync<PortBindingException>(() => enc.Encrypt(Sif, "another-key", "f.sif"));
        Assert.Contains("keys.another-key.secretRef", e.Message);
        var literal = new PortBinding("test-bank", OpenPgpEncryption.Port, OpenPgpEncryption.Name, From, new Dictionary<string, string> { ["keys.cbuae-wps.secretRef"] = "-----BEGIN PGP PUBLIC KEY BLOCK-----" });
        var refused = await Assert.ThrowsAsync<PortBindingException>(() => new OpenPgpEncryption(literal, new SecretResolver([])).Encrypt(Sif, "cbuae-wps", "f.sif"));
        Assert.Contains("must be a reference", refused.Message);
        Assert.DoesNotContain("BEGIN PGP", refused.Message);
        Assert.Throws<PortBindingException>(() => FileProtectionAdapters.Encryption(literal with { Adapter = "gnupg" }));
    }

    static string Folder(string bank) => PlatformOnlyBankFolderTests.Folder(bank);

    static YamlMappingNode ConfigurationSet(string bank)
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(File.ReadAllText(Path.Combine(Folder(bank), "configuration-set.yaml"))));
        return (YamlMappingNode)ys.Documents[0].RootNode;
    }

    static string? Value(YamlMappingNode cs, string parameter) =>
        ((YamlSequenceNode)cs["values"]).Children.Cast<YamlMappingNode>().Where(v => ((YamlScalarNode)v["parameter"]).Value == parameter)
            .Select(v => v["value"] is YamlScalarNode s ? s.Value : v["value"].ToString()).LastOrDefault();

    [Theory]
    [InlineData("dib")]
    [InlineData("adib")]
    public void The_bank_encrypts_for_the_key_its_configuration_names_and_binds_that_key_by_reference_only(string bank)
    {
        var b = PortBindings.FromDirectory(Folder(bank)).InForce(bank, OpenPgpEncryption.Port, From);
        Assert.Equal(OpenPgpEncryption.Name, b.Adapter);
        var keyId = Value(ConfigurationSet(bank), "p.protect.encrypt-file.v1#recipientKey");
        Assert.Equal("cbuae-wps", keyId);
        var reference = new OpenPgpEncryption(b, new SecretResolver([])).KeyReference(keyId!);
        Assert.Equal($"env:TH_{bank.ToUpperInvariant()}_CB_WPS_PUBLIC_KEY", reference);
        foreach (var (k, v) in b.Settings)
            if (k.EndsWith("secretRef", StringComparison.Ordinal)) Assert.True(SecretRef.IsRef(v), k);
        Assert.Equal(["armor", "keys.cbuae-wps.secretRef"], b.Settings.Keys.Order(StringComparer.Ordinal));
    }
}

// ADIB runs WPS by the owner's decision of 2026-11-22 (Issue I-86): its folder binds the file transfer the source's WPS fork
// used, by placeholder host and credential reference, and keeps the core banking it cannot describe refused (Issue I-85).
public sealed class AdibWpsBankFolderTests
{
    const string Tenant = "adib";
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-22T00:00:00Z");
    static string Folder() => PlatformOnlyBankFolderTests.Folder(Tenant);

    [Fact]
    public void The_folder_binds_core_banking_file_transfer_encryption_and_the_labour_register_and_nothing_else()
    {
        var bindings = PortBindings.FromDirectory(Folder());
        // The labour ministry register (Issue I-118) is bound to the REST adapter with a placeholder host until the bank says where it is.
        // The party register (R-12-27) is the runtime's own, read from the store.
        Assert.Equal(["p.port.core-banking.v1", "p.port.encryption.v1", "p.port.file-transfer.v1", "p.port.party-register.v1", "uae.wps.port.labour-ministry.v1"], bindings.All.Select(x => x.Port).Order(StringComparer.Ordinal));
        Assert.All(bindings.All, x => Assert.Equal(Tenant, x.Tenant));
        Assert.All(bindings.All.Where(x => x.Port != PartyRegisterAdapters.Port), x => Assert.Equal(From, x.ValidFrom));
        Assert.Equal(StorePartyRegister.Name, bindings.All.Single(x => x.Port == PartyRegisterAdapters.Port).Adapter);
        // No decryption: the source's WPS workflow for the bank reads unencrypted files (fileEncrypted false), and its
        // decrypt step names no key (AdibDecryptSifFile).
        Assert.False(File.Exists(Path.Combine(Folder(), "decryption.yaml")));
    }

    [Fact]
    public void The_file_transfer_carries_the_folders_of_the_source_with_placeholder_hosts_and_a_credential_by_reference()
    {
        var b = PortBindings.FromDirectory(Folder()).InForce(Tenant, "p.port.file-transfer.v1", From);
        Assert.Equal("sftp", b.Adapter);
        Assert.Equal("/wps/inbound", b.Settings["pickup.folder"]);
        Assert.Equal("/wps/outbound", b.Settings["result.folder"]);
        Assert.Equal("/cb/wps/ack", b.Settings["centralBank.ackFolder"]);
        Assert.Equal("/cb/wps/rtc", b.Settings["centralBank.rtcFolder"]);
        Assert.Equal("env:TH_ADIB_SFTP_CREDENTIAL", b.Settings["secretRef"]);
        foreach (var (k, v) in b.Settings)
            if (k.EndsWith("host", StringComparison.OrdinalIgnoreCase)) Assert.EndsWith(".invalid", v);
    }

    [Fact]
    public void The_bank_runs_WPS_with_no_routing_code_and_binds_the_labour_ministry_notification_only()
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(File.ReadAllText(Path.Combine(Folder(), "configuration-set.yaml"))));
        var root = (YamlMappingNode)ys.Documents[0].RootNode;
        var values = ((YamlSequenceNode)root["values"]).Children.Cast<YamlMappingNode>().ToList();
        var rails = Assert.Single(values, v => ((YamlScalarNode)v["parameter"]).Value == "p.table.rail-choice.v1#enabledRails");
        Assert.Equal(["uae.wps"], ((YamlSequenceNode)rails["value"]).Children.Select(c => ((YamlScalarNode)c).Value));
        Assert.DoesNotContain(values, v => ((YamlScalarNode)v["parameter"]).Value == "uae.wps.rule.agent-is-this-bank.v1#ownRoutingCodes");
        // The source holds no CBX values or routing code for the bank: there is no rules file for the runtime to read.
        Assert.False(File.Exists(Path.Combine(Folder(), "uae.wps.salary-file-rules.yaml")));
        var binding = Assert.Single(((YamlSequenceNode)root["bindings"]).Children.Cast<YamlMappingNode>());
        Assert.Equal("b.hook.labour-ministry-notify.v1", ((YamlScalarNode)binding["hook"]).Value);
        Assert.Equal("p.wf.corporate.v1#on-close", ((YamlScalarNode)binding["hookPoint"]).Value);
    }
}
