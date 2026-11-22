using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Wps;
using TransactHub.Activities.Wps.Rules;
using TransactHub.Ports;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace TransactHub.Store.Tests;

// A real bank's configuration in its own folder of this repository (plan W6; D5 as amended on 2026-11-21):
// content-configurations/banks/<bank>/. The runtime reads its core banking from the folder's port bindings and its
// WPS rule values from the folder's configuration values. Shared code still names no bank (BankConfigurationTests).

public sealed class BankFolderConfigurationTests
{
    const string Tenant = "dib";
    const string Port = "p.port.core-banking.v1";
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-21T00:00:00Z");

    static string Folder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations")) && Directory.Exists(Path.Combine(d.FullName, "runtime")))
                return Path.Combine(d.FullName, "content-configurations", "banks", Tenant);
        throw new DirectoryNotFoundException("repository root");
    }

    [Fact]
    public void The_bank_folder_binds_core_banking_for_its_tenant_and_no_other()
    {
        var bindings = PortBindings.FromDirectory(Folder());
        var b = bindings.InForce(Tenant, Port, From);
        Assert.Equal(GatewayRestCoreBanking.Name, b.Adapter);
        Assert.IsType<GatewayRestCoreBanking>(CoreBankingAdapters.Create(b, new HttpClient(new Recorder())));
        // From its validFrom only, and for its own tenant only: no bank is defaulted.
        Assert.Throws<PortBindingException>(() => bindings.InForce(Tenant, Port, From.AddSeconds(-1)));
        Assert.Throws<PortBindingException>(() => bindings.InForce("example-bank", Port, From));
        Assert.All(bindings.All, x => Assert.Equal(Tenant, x.Tenant));
    }

    [Fact]
    public async Task The_bank_settings_the_source_read_from_CoreBanking_reach_the_wire()
    {
        var b = PortBindings.FromDirectory(Folder()).InForce(Tenant, Port, DateTimeOffset.Parse("2026-11-24T08:00:00Z"));
        var rec = new Recorder();
        // The gateway credential, by reference: a synthetic value for the test (R-06-22).
        var token = "synthetic-" + Guid.NewGuid().ToString("N");
        var cb = CoreBankingAdapters.Create(b, new HttpClient(rec), TransactHub.Security.SecretResolver.FromEnvironment(k => k == "TH_DIB_CORE_BANKING_TOKEN" ? token : null));
        // Synthetic accounts: the bank's settings are real, the payment is not.
        var batch = new PostingBatch("F0001", new DateOnly(2026, 11, 24), [
            new("debit", "001", "0010000000001", 3500.00m, "AED", "SALARY", "Salary file F0001"),
            new("credit", "997", "9970000000001", 3500.00m, "AED", "SALARY", "Salary file F0001"),
        ]);
        var answer = await cb.Post(batch);
        Assert.True(answer.Posted);

        var (req, body) = rec.Requests.Single();
        Assert.Equal("https://core-banking.dib.invalid/api/v1/accounts/transactions", req.RequestUri!.ToString());
        Assert.Equal("0332", req.Headers.GetValues("X-Function-Id").Single());
        Assert.Equal("IBMK", req.Headers.GetValues("X-External-Host-Id").Single());
        Assert.Equal("Y", req.Headers.GetValues("X-Txn-Flag").Single());
        Assert.Equal("WPS", req.Headers.GetValues("X-Channel").Single());
        var j = (JsonObject)JsonNode.Parse(body)!;
        Assert.Equal("IBSSYSTEM", (string?)j["serviceCode"]);
        Assert.Equal("997", (string?)j["uploadBranch"]);
        Assert.Equal("Y", (string?)j["balanceFlag"]);
        Assert.Equal("A", (string?)j["authStatus"]);
        Assert.Equal("DIBF0001", (string?)j["reference"]);

        await cb.Balance("001", "0010000000001");
        Assert.Equal("https://core-banking.dib.invalid/api/v1/accounts/balance-enquiry", rec.Requests[1].req.RequestUri!.ToString());
        Assert.Equal("0301", rec.Requests[1].req.Headers.GetValues("X-Function-Id").Single());
    }

    [Fact]
    public void The_bank_values_for_the_WPS_salary_file_rules_are_complete_and_inside_their_bounds()
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(File.ReadAllText(Path.Combine(Folder(), "uae.wps.salary-file-rules.yaml"))));
        var root = (YamlMappingNode)ys.Documents[0].RootNode;
        Assert.Equal("configuration-values", ((YamlScalarNode)root["kind"]).Value);
        Assert.Equal(Tenant, ((YamlScalarNode)root["tenant"]).Value);
        var values = ((YamlMappingNode)root["values"]).Children.ToDictionary(k => ((YamlScalarNode)k.Key).Value!, v => ((YamlScalarNode)v.Value).Value);
        Assert.Equal(WpsRuleValues.Parameters.Order(), values.Keys.Order());

        var (settings, invalid) = CbxRuleSettings.FromParameters(values);
        Assert.Null(invalid);
        Assert.Equal(13, settings!.MolIdLength);
        Assert.Equal(15, settings.AccountMatchLength);
        Assert.Equal("802420101", values[CbxScrRules.BankRoutingCodeParam]);
        // The bank's routing code decides rule 66 on a synthetic control record.
        var scr = new[] { "SCR", "0000001234567", "802420101", "2026-11-24", "0830", "102026", "2", "3500.00", "AED", "000000000000001" };
        Assert.Null(CbxScrRules.CheckBankRoutingCode(scr, values[CbxScrRules.BankRoutingCodeParam]!, 3));
        Assert.NotNull(CbxScrRules.CheckBankRoutingCode(scr, "302620122", 3));
    }

    [Theory]
    [InlineData("p.port.file-transfer.v1", "sftp", "centralBank.sifFolder", "/sftp_user/wps/DIB/SIF/ToBeDone")]
    [InlineData("p.port.decryption.v1", "gnupg", "key", "DibWpsInbound")]
    public void The_bank_folder_binds_its_file_transfer_and_decryption_with_secrets_by_reference_only(string port, string adapter, string setting, string value)
    {
        var b = PortBindings.FromDirectory(Folder()).InForce(Tenant, port, From);
        Assert.Equal(adapter, b.Adapter);
        Assert.Equal(value, b.Settings[setting]);
        Assert.StartsWith("env:TH_DIB_", b.Settings["secretRef"]);
        // Every host is a placeholder a deployment replaces, and no setting carries a secret's value.
        foreach (var (k, v) in b.Settings)
        {
            if (k.EndsWith("host", StringComparison.OrdinalIgnoreCase)) Assert.EndsWith(".invalid", v);
            if (k.Contains("secret", StringComparison.OrdinalIgnoreCase) || k.Contains("password", StringComparison.OrdinalIgnoreCase) || k.EndsWith("Ref", StringComparison.Ordinal))
                Assert.StartsWith("env:", v);
        }
    }

    /// <summary>A core banking behind a gateway, as far as the adapter can tell: it records each request and answers by function id.</summary>
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage req, string body)> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            var posting = request.Headers.GetValues("X-Function-Id").Single() == "0332";
            var answer = posting ? "{\"code\":\"0\",\"hostReference\":\"H-1\"}" : "{\"available\":5000.00,\"currency\":\"AED\",\"status\":\"NORM\"}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
