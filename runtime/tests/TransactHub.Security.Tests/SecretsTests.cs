using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TransactHub.Security;

namespace TransactHub.Security.Tests;

// The one secret interface (spec R-06-22): env:, file: and vault: references, resolved by the host; the value never
// appears in a message, an exception or a printed object.

public sealed class SecretsTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("th-secrets-").FullName;
    public void Dispose() => Directory.Delete(dir, recursive: true);

    static string Synthetic() => "synthetic-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task An_env_reference_reads_the_variable_and_an_unset_one_says_so_without_a_value()
    {
        var value = Synthetic();
        var r = SecretResolver.FromEnvironment(k => k == "TH_TEST_TOKEN" ? value : null);
        Assert.Equal(value, (await r.Resolve("env:TH_TEST_TOKEN")).Reveal());
        var e = await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve("env:TH_TEST_MISSING"));
        Assert.Equal("Secret env:TH_TEST_MISSING is not set", e.Message);
    }

    [Fact]
    public async Task A_file_reference_reads_a_mounted_secret_inside_its_roots_only()
    {
        var value = Synthetic();
        var f = Path.Combine(dir, "token");
        await File.WriteAllTextAsync(f, value + "\n");
        var r = SecretResolver.FromEnvironment(k => k == "TH_SECRET_FILE_ROOTS" ? dir : null);
        Assert.Equal(value, (await r.Resolve($"file:{f}")).Reveal());
        Assert.Contains("outside the folders", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve("file:/etc/hostname"))).Message);
        Assert.Contains("without '..'", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve($"file:{dir}/../x"))).Message);
        Assert.Contains("without '..'", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve("file:relative/path"))).Message);
        Assert.Contains("cannot be read", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve($"file:{dir}/missing"))).Message);
    }

    [Fact]
    public async Task A_value_is_never_a_reference_and_is_never_shown()
    {
        var value = Synthetic();
        var r = SecretResolver.FromEnvironment(_ => null);
        // A literal where a reference belongs: refused, and the message gives only its length.
        var e = await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve(value));
        Assert.DoesNotContain(value, e.Message);
        Assert.Contains($"{value.Length} characters", e.Message);
        // A value printed by mistake shows a mark.
        var s = new SecretValue(value);
        Assert.Equal("[secret]", s.ToString());
        Assert.DoesNotContain(value, $"{s}");
        Assert.DoesNotContain(value, System.Text.Json.JsonSerializer.Serialize(s));
        // Azure Key Vault is named but not built (Issue I-73); an unknown store is refused.
        Assert.Contains("I-73", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve("akv:vault-name/secret"))).Message);
        Assert.Contains("has not configured", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve("vault:secret/th#x"))).Message);
    }
}

/// <summary>
/// The Vault provider against a real HashiCorp Vault (the dev server in a container, its root token made for this run):
/// a KV version 2 secret read by reference, through a token that is itself read by reference.
/// </summary>
public sealed class VaultTests : IAsyncLifetime
{
    readonly string rootToken = Guid.NewGuid().ToString("N");
    IContainer? vault;
    string addr = "";

    public async Task InitializeAsync()
    {
        vault = new ContainerBuilder("hashicorp/vault:1.18.3")
            .WithName($"th-vault-test-{Guid.NewGuid():N}"[..28])
            .WithEnvironment("VAULT_DEV_ROOT_TOKEN_ID", rootToken)
            .WithEnvironment("VAULT_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
            .WithCommand("server", "-dev")
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")))
            .Build();
        await vault.StartAsync();
        addr = $"http://{vault.Hostname}:{vault.GetMappedPublicPort(8200)}/";
    }

    public async Task DisposeAsync() { if (vault is not null) await vault.DisposeAsync(); }

    [Fact]
    public async Task A_vault_reference_reads_a_field_of_a_KV2_secret_and_a_wrong_one_says_why_without_a_value()
    {
        var value = "synthetic-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient();
        using (var put = new HttpRequestMessage(HttpMethod.Post, $"{addr}v1/secret/data/transacthub/test") { Content = JsonContent.Create(new { data = new { signingKey = value } }) })
        {
            put.Headers.Add("X-Vault-Token", rootToken);
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(put)).StatusCode);
        }
        var env = new Dictionary<string, string> { ["TH_VAULT_ADDR"] = addr, ["TH_VAULT_TOKEN"] = "env:TH_TEST_VAULT_TOKEN", ["TH_TEST_VAULT_TOKEN"] = rootToken };
        var r = SecretResolver.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(value, (await r.Resolve("vault:secret/transacthub/test#signingKey")).Reveal());

        var noField = await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve("vault:secret/transacthub/test#other"));
        Assert.Contains("has no field other", noField.Message);
        var noSecret = await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve("vault:secret/transacthub/none#signingKey"));
        Assert.Contains("Vault answered 404", noSecret.Message);
        // A wrong token: Vault refuses, and neither token nor value is in the message.
        env["TH_TEST_VAULT_TOKEN"] = "not-the-token";
        var refused = await Assert.ThrowsAsync<SecretUnavailable>(() => SecretResolver.FromEnvironment(k => env.GetValueOrDefault(k)).Resolve("vault:secret/transacthub/test#signingKey"));
        Assert.Contains("Vault answered 403", refused.Message);
        foreach (var e in new[] { noField, noSecret, refused })
        {
            Assert.DoesNotContain(value, e.ToString());
            Assert.DoesNotContain(rootToken, e.ToString());
        }
        Assert.Contains("TH_VAULT_TOKEN", Assert.Throws<SecretUnavailable>(() => SecretResolver.FromEnvironment(k => k == "TH_VAULT_ADDR" ? addr : null)).Message);
    }
}
