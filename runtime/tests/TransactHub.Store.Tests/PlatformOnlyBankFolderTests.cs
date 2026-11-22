using System.Net;
using TransactHub.Ports;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace TransactHub.Store.Tests;

// Three real banks' folders in this repository (owner's decision of 2026-11-22, D5 as amended): content-configurations/
// banks/<bank>/. None of them runs WPS in the source, so each configures the platform only. The source holds no core-banking
// setting of theirs (every route their forks called is the bank simulator's, Issue I-85), so each binds core banking to the
// gateway with a placeholder host and a credential by reference, and the adapter refuses every call until the bank's
// settings are added. Nothing is defaulted. Shared code still names no bank (BankConfigurationTests).

public sealed class PlatformOnlyBankFolderTests
{
    const string Port = "p.port.core-banking.v1";
    static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-11-22T00:00:00Z");

    public static TheoryData<string> Banks => new() { "adib", "enbd", "fab" };

    static string Folder(string tenant)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "content-configurations")) && Directory.Exists(Path.Combine(d.FullName, "runtime")))
                return Path.Combine(d.FullName, "content-configurations", "banks", tenant);
        throw new DirectoryNotFoundException("repository root");
    }

    [Theory]
    [MemberData(nameof(Banks))]
    public void The_folder_binds_core_banking_for_its_tenant_from_its_validFrom_and_no_other(string tenant)
    {
        var bindings = PortBindings.FromDirectory(Folder(tenant));
        var b = bindings.InForce(tenant, Port, From);
        Assert.Equal(GatewayRestCoreBanking.Name, b.Adapter);
        Assert.Throws<PortBindingException>(() => bindings.InForce(tenant, Port, From.AddSeconds(-1)));
        Assert.Throws<PortBindingException>(() => bindings.InForce("example-bank", Port, From));
        Assert.All(bindings.All, x => Assert.Equal(tenant, x.Tenant));
        // Core banking is the only port bound: the bank's file transfer and decryption are WPS's, which it does not run.
        Assert.Equal([Port], bindings.All.Select(x => x.Port).Distinct());
    }

    [Theory]
    [MemberData(nameof(Banks))]
    public async Task Until_the_bank_settings_are_added_every_call_is_refused_and_nothing_reaches_the_wire(string tenant)
    {
        var b = PortBindings.FromDirectory(Folder(tenant)).InForce(tenant, Port, DateTimeOffset.Parse("2026-11-24T08:00:00Z"));
        Assert.EndsWith(".invalid/", b.Settings["baseUrl"]);
        Assert.Equal($"env:TH_{tenant.ToUpperInvariant()}_CORE_BANKING_TOKEN", b.Settings["secretRef"]);
        Assert.Equal(["baseUrl", "secretRef"], b.Settings.Keys.Order(StringComparer.Ordinal));

        var rec = new Recorder();
        var cb = CoreBankingAdapters.Create(b, new HttpClient(rec));
        // Synthetic accounts.
        var batch = new PostingBatch("F0001", new DateOnly(2026, 11, 24), [new("debit", "001", "0010000000001", 3500.00m, "AED", "PAY", "Salary file F0001")]);
        var post = await Assert.ThrowsAsync<PortBindingException>(() => cb.Post(batch));
        Assert.Contains("has no '", post.Message);
        await Assert.ThrowsAsync<PortBindingException>(() => cb.Balance("001", "0010000000001"));
        Assert.Empty(rec.Requests);
    }

    [Theory]
    [MemberData(nameof(Banks))]
    public void The_bank_runs_no_factory_rail_and_carries_no_WPS_rule_values(string tenant)
    {
        Assert.False(File.Exists(Path.Combine(Folder(tenant), "uae.wps.salary-file-rules.yaml")));
        var ys = new YamlStream();
        ys.Load(new StringReader(File.ReadAllText(Path.Combine(Folder(tenant), "configuration-set.yaml"))));
        var root = (YamlMappingNode)ys.Documents[0].RootNode;
        Assert.Equal(tenant, ((YamlScalarNode)root["tenant"]).Value);
        var values = ((YamlSequenceNode)root["values"]).Children.Cast<YamlMappingNode>().ToList();
        var rails = Assert.Single(values, v => ((YamlScalarNode)v["parameter"]).Value == "p.table.rail-choice.v1#enabledRails");
        Assert.Empty((YamlSequenceNode)rails["value"]);
        Assert.DoesNotContain(values, v => ((YamlScalarNode)v["parameter"]).Value!.StartsWith("uae.wps.", StringComparison.Ordinal));
        Assert.Empty((YamlSequenceNode)root["bindings"]);
    }

    /// <summary>Records each request; a call that reaches it is a failure of the refusal.</summary>
    sealed class Recorder : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
