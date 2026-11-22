using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TransactHub.Security;

namespace TransactHub.Security.Tests;

// The cloud secret stores (spec R-06-26, R-06-27): Azure Key Vault on its official SDK against an in-process fake of its
// REST protocol (FakeKeyVault: there is no official emulator), and AWS Secrets Manager and Parameter Store on theirs
// against LocalStack in a container of this run's own. Every value, token and key here is made by the test.

sealed class StaticToken(Func<string> issue) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new(issue(), DateTimeOffset.UtcNow.AddHours(1));
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => ValueTask.FromResult(GetToken(c, ct));
}

sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class AzureKeyVaultTests
{
    static string Synthetic() => "synthetic-" + Guid.NewGuid().ToString("N");
    // One tenant for the process: the SDK keeps each vault's challenge (and so its tenant) in a static cache, as a vault's
    // tenant does not change.
    static readonly string tenantId = Guid.NewGuid().ToString();
    readonly string clientId = Guid.NewGuid().ToString();
    readonly string clientSecret = Synthetic();
    readonly FakeKeyVault fake;
    readonly string value = Synthetic();
    readonly string older = Synthetic();
    readonly string olderVersion;

    public AzureKeyVaultTests()
    {
        fake = new FakeKeyVault(tenantId, clientId, clientSecret);
        olderVersion = fake.Set("th-test-vault", "signing-key", older);
        fake.Set("th-test-vault", "signing-key", value);
    }

    HttpPipelineTransport Transport => new HttpClientTransport(fake);
    SecretResolver With(TokenCredential c) => new([new AzureKeyVaultSecretProvider(_ => Task.FromResult(c), transport: Transport)]);

    [Fact]
    public async Task An_akv_reference_reads_the_latest_version_and_a_named_one_through_the_bearer_challenge()
    {
        var r = With(new StaticToken(() => fake.Issue()));
        Assert.Equal(value, (await r.Resolve("akv:th-test-vault/signing-key")).Reveal());
        Assert.Equal(older, (await r.Resolve($"akv:th-test-vault/signing-key#{olderVersion}")).Reveal());
        // Asked once without a token, challenged, and asked again with one.
        Assert.True(fake.Requests.Count(q => q.StartsWith("GET https://th-test-vault.vault.azure.net/secrets/signing-key")) >= 3);
        Assert.Empty(fake.Unknown);
    }

    [Fact]
    public async Task Client_credentials_sign_in_with_a_secret_that_is_itself_a_reference_and_a_wrong_one_says_why_without_a_value()
    {
        var env = new Dictionary<string, string>
        {
            ["AZURE_TENANT_ID"] = tenantId, ["AZURE_CLIENT_ID"] = clientId, ["AZURE_AUTHORITY_HOST"] = FakeKeyVault.Authority,
            ["TH_AKV_CLIENT_SECRET"] = "env:TH_TEST_AKV_CLIENT_SECRET", ["TH_TEST_AKV_CLIENT_SECRET"] = clientSecret,
        };
        async Task<SecretResolver> From(Dictionary<string, string> e)
        {
            var bootstrap = SecretResolver.FromEnvironment(k => e.GetValueOrDefault(k));
            return new SecretResolver([new AzureKeyVaultSecretProvider(ct => AzureKeyVaultSecretProvider.CredentialFrom(k => e.GetValueOrDefault(k), bootstrap, Transport, ct), transport: Transport)]);
        }
        Assert.Equal(value, (await (await From(env)).Resolve("akv:th-test-vault/signing-key")).Reveal());
        Assert.Contains(clientSecret, fake.ClientSecretsSeen);
        Assert.Empty(fake.Unknown);

        var wrong = Synthetic();
        var e = await Assert.ThrowsAsync<SecretUnavailable>(async () => await (await From(new(env) { ["TH_TEST_AKV_CLIENT_SECRET"] = wrong })).Resolve("akv:th-test-vault/signing-key"));
        Assert.Equal("Secret akv:th-test-vault/signing-key could not be read: no Azure credential could get a token (client credentials, workload identity or managed identity)", e.Message);
        foreach (var s in new[] { wrong, clientSecret, value }) Assert.DoesNotContain(s, e.ToString());
        // The client secret is a reference to env: or file:, never the secret itself, and never AZURE_CLIENT_SECRET.
        var literal = await Assert.ThrowsAsync<SecretUnavailable>(async () => await (await From(new(env) { ["TH_AKV_CLIENT_SECRET"] = wrong })).Resolve("akv:th-test-vault/signing-key"));
        Assert.DoesNotContain(wrong, literal.ToString());
        Assert.Contains("must be a reference to env: or file:", literal.Message);
        var azureSecret = await Assert.ThrowsAsync<SecretUnavailable>(async () => await (await From(new() { ["AZURE_CLIENT_SECRET"] = wrong })).Resolve("akv:th-test-vault/signing-key"));
        Assert.Contains("set TH_AKV_CLIENT_SECRET to a reference", azureSecret.Message);
        Assert.DoesNotContain(wrong, azureSecret.ToString());
    }

    [Fact]
    public async Task A_missing_secret_a_refusal_and_a_malformed_reference_name_the_reference_only()
    {
        var e = await Assert.ThrowsAsync<SecretUnavailable>(() => With(new StaticToken(() => fake.Issue())).Resolve("akv:th-test-vault/no-such-secret"));
        Assert.Equal("Secret akv:th-test-vault/no-such-secret does not exist: Key Vault answered 404 SecretNotFound", e.Message);
        var refused = await Assert.ThrowsAsync<SecretUnavailable>(() => With(new StaticToken(() => fake.Issue(granted: false))).Resolve("akv:th-test-vault/signing-key"));
        Assert.Equal("Secret akv:th-test-vault/signing-key could not be read: Key Vault refused access (403 Forbidden)", refused.Message);
        Assert.DoesNotContain(value, refused.ToString());
        foreach (var bad in new[] { "akv:v/secret", "akv:th-test-vault", "akv:th-test-vault/a_b", "akv:th-test-vault/x#not-a-version" })
            Assert.Contains("is not akv:<vault-name>/<secret-name>[#version]", (await Assert.ThrowsAsync<SecretUnavailable>(() => With(new StaticToken(() => fake.Issue())).Resolve(bad))).Message);
    }

    [Fact]
    public async Task The_credential_is_chosen_in_DefaultAzureCredential_order_with_client_secrets_by_reference_only()
    {
        async Task<string> Kind(Dictionary<string, string> e) => (await AzureKeyVaultSecretProvider.CredentialFrom(k => e.GetValueOrDefault(k), SecretResolver.FromEnvironment(k => e.GetValueOrDefault(k)))).GetType().Name;
        Assert.Equal(nameof(ManagedIdentityCredential), await Kind([]));
        Assert.Equal(nameof(ManagedIdentityCredential), await Kind(new() { ["AZURE_CLIENT_ID"] = clientId }));
        Assert.Equal(nameof(WorkloadIdentityCredential), await Kind(new() { ["AZURE_TENANT_ID"] = tenantId, ["AZURE_CLIENT_ID"] = clientId, ["AZURE_FEDERATED_TOKEN_FILE"] = "/var/run/secrets/azure/tokens/azure-identity-token" }));
        Assert.Equal(nameof(ClientSecretCredential), await Kind(new() { ["AZURE_TENANT_ID"] = tenantId, ["AZURE_CLIENT_ID"] = clientId, ["TH_AKV_CLIENT_SECRET"] = "env:S", ["S"] = Synthetic() }));
        Assert.Contains("AZURE_TENANT_ID and AZURE_CLIENT_ID", (await Assert.ThrowsAsync<SecretUnavailable>(() => Kind(new() { ["TH_AKV_CLIENT_SECRET"] = "env:S" }))).Message);
    }

    [Fact]
    public async Task A_value_read_over_the_network_is_kept_for_its_time_only_and_read_again_when_refused()
    {
        var clock = new ManualClock();
        var caching = new CachingSecretProvider(new AzureKeyVaultSecretProvider(_ => Task.FromResult<TokenCredential>(new StaticToken(() => fake.Issue())), transport: Transport), TimeSpan.FromSeconds(60), clock);
        var r = new SecretResolver([caching]);
        int Reads() => fake.Requests.Count(q => q.Contains("/secrets/rotating"));
        var first = Synthetic();
        fake.Set("th-test-vault", "rotating", first);
        Assert.Equal(first, (await r.Resolve("akv:th-test-vault/rotating")).Reveal());
        var n = Reads();
        var second = Synthetic();
        fake.Set("th-test-vault", "rotating", second);
        clock.Now += TimeSpan.FromSeconds(59);
        Assert.Equal(first, (await r.Resolve("akv:th-test-vault/rotating")).Reveal());
        Assert.Equal(n, Reads());
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Equal(second, (await r.Resolve("akv:th-test-vault/rotating")).Reveal());

        // A credential the system it opens refuses is read again, once, and the rotated value used.
        var third = Synthetic();
        fake.Set("th-test-vault", "rotating", third);
        var used = new List<string>();
        var refusal = new InvalidOperationException("401 from the system the token opens");
        var result = await r.WithSecret("akv:th-test-vault/rotating", v =>
        {
            used.Add(v.Reveal());
            return v.Reveal() == third ? Task.FromResult("posted") : throw refusal;
        }, e => e == refusal);
        Assert.Equal("posted", result);
        Assert.Equal([second, third], used);
        await Assert.ThrowsAsync<ArgumentException>(() => r.WithSecret<string>("akv:th-test-vault/rotating", _ => throw new ArgumentException("other"), _ => false));

        Assert.Equal(TimeSpan.FromMinutes(5), SecretResolver.CacheTtl(_ => null));
        Assert.Equal(TimeSpan.Zero, SecretResolver.CacheTtl(_ => "0"));
        Assert.Throws<SecretUnavailable>(() => SecretResolver.CacheTtl(_ => "-1"));
    }

    [Fact]
    public void Every_reference_form_is_a_reference_and_an_unknown_store_is_not()
    {
        foreach (var r in new[] { "akv:v1v/s", "aws-sm:eu-west-1/th/db#password", "aws-ssm:eu-west-1/th/token", "vault:secret/x#y" }) Assert.True(SecretRef.IsRef(r), r);
        Assert.False(SecretRef.IsRef("gcp:projects/x"));
        Assert.Equal("/th/token", AwsParameterStoreSecretProvider.ParameterName("th/token"));
        Assert.Equal("/th/token", AwsParameterStoreSecretProvider.ParameterName("//th/token"));
        Assert.Equal("token", AwsParameterStoreSecretProvider.ParameterName("token"));
    }
}

/// <summary>LocalStack, pinned, in a container named for this run, with Secrets Manager and Systems Manager.</summary>
public sealed class LocalStack : IAsyncLifetime
{
    public const string Image = "localstack/localstack:4.14.0";
    IContainer? container;
    public string Endpoint { get; private set; } = "";
    // LocalStack takes any credentials; these are made for this run.
    public BasicAWSCredentials Credentials { get; } = new("AKIA" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(), "synthetic-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        container = new ContainerBuilder(Image)
            .WithName($"th-secrets-localstack-{Guid.NewGuid():N}"[..34])
            .WithEnvironment("SERVICES", "secretsmanager,ssm,kms")
            .WithPortBinding(4566, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(4566).ForPath("/_localstack/health")))
            .Build();
        await container.StartAsync();
        Endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(4566)}";
    }

    public async Task DisposeAsync() { if (container is not null) await container.DisposeAsync(); }

    public IAmazonSecretsManager SecretsManager(string region, AWSCredentials? creds = null) => new AmazonSecretsManagerClient(creds ?? Credentials, new AmazonSecretsManagerConfig { ServiceURL = Endpoint, AuthenticationRegion = region, MaxErrorRetry = 0 });
    public IAmazonSimpleSystemsManagement ParameterStore(string region) => new AmazonSimpleSystemsManagementClient(Credentials, new AmazonSimpleSystemsManagementConfig { ServiceURL = Endpoint, AuthenticationRegion = region });
}

sealed class NoCredentials : AWSCredentials
{
    public override ImmutableCredentials GetCredentials() => throw new AmazonClientException("Failed to resolve AWS credentials in this test");
}

public sealed class AwsSecretsTests(LocalStack aws) : IClassFixture<LocalStack>
{
    const string Region = "eu-west-1";
    static string Synthetic() => "synthetic-" + Guid.NewGuid().ToString("N");
    SecretResolver Resolver() => new([new AwsSecretsManagerSecretProvider(region => aws.SecretsManager(region)), new AwsParameterStoreSecretProvider(aws.ParameterStore)]);

    [Fact]
    public async Task An_aws_sm_reference_reads_a_secret_string_by_name_and_ARN_and_a_field_of_a_JSON_secret()
    {
        var id = $"th/test/{Guid.NewGuid():N}";
        var value = Synthetic();
        var field = Synthetic();
        using var sm = aws.SecretsManager(Region);
        var arn = (await sm.CreateSecretAsync(new CreateSecretRequest { Name = id, SecretString = value })).ARN;
        await sm.CreateSecretAsync(new CreateSecretRequest { Name = id + "-json", SecretString = $"{{\"password\":\"{field}\",\"port\":5432}}" });
        await sm.CreateSecretAsync(new CreateSecretRequest { Name = id + "-binary", SecretBinary = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Synthetic())) });
        var r = Resolver();
        Assert.Equal(value, (await r.Resolve($"aws-sm:{Region}/{id}")).Reveal());
        Assert.Equal(value, (await r.Resolve($"aws-sm:{Region}/{arn}")).Reveal());
        Assert.Equal(field, (await r.Resolve($"aws-sm:{Region}/{id}-json#password")).Reveal());
        Assert.Equal("5432", (await r.Resolve($"aws-sm:{Region}/{id}-json#port")).Reveal());

        async Task<string> Why(string reference, SecretResolver? with = null) => (await Assert.ThrowsAsync<SecretUnavailable>(() => (with ?? r).Resolve(reference))).Message;
        Assert.Equal($"Secret aws-sm:{Region}/{id}-none does not exist: Secrets Manager answered ResourceNotFoundException", await Why($"aws-sm:{Region}/{id}-none"));
        Assert.Contains("does not exist", await Why($"aws-sm:us-east-1/{id}"));
        Assert.Contains("is a binary secret", await Why($"aws-sm:{Region}/{id}-binary"));
        var noField = await Why($"aws-sm:{Region}/{id}-json#user");
        Assert.Equal($"Secret aws-sm:{Region}/{id}-json#user has no field user", noField);
        Assert.DoesNotContain(field, noField);
        Assert.Contains("is not a JSON object", await Why($"aws-sm:{Region}/{id}#password"));
        Assert.Contains("is not aws-sm:<region>/<secret-id>[#json-field]", await Why($"aws-sm:nowhere/{id}"));
        var noCreds = new SecretResolver([new AwsSecretsManagerSecretProvider(region => aws.SecretsManager(region, new NoCredentials()))]);
        Assert.Contains("no AWS credentials were found", await Why($"aws-sm:{Region}/{id}", noCreds));
    }

    [Fact]
    public async Task An_aws_ssm_reference_reads_a_SecureString_by_its_path_and_refuses_a_plain_String()
    {
        var path = $"/th/test/{Guid.NewGuid():N}";
        var value = Synthetic();
        using var ssm = aws.ParameterStore(Region);
        await ssm.PutParameterAsync(new PutParameterRequest { Name = path + "/token", Value = value, Type = ParameterType.SecureString });
        await ssm.PutParameterAsync(new PutParameterRequest { Name = path + "/plain", Value = Synthetic(), Type = ParameterType.String });
        var r = Resolver();
        Assert.Equal(value, (await r.Resolve($"aws-ssm:{Region}{path}/token")).Reveal());
        Assert.Equal(value, (await r.Resolve($"aws-ssm:{Region}/{path}/token")).Reveal());
        Assert.Contains("is a String, not a SecureString", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve($"aws-ssm:{Region}{path}/plain"))).Message);
        Assert.Equal($"Secret aws-ssm:{Region}{path}/none does not exist: Parameter Store answered ParameterNotFound", (await Assert.ThrowsAsync<SecretUnavailable>(() => r.Resolve($"aws-ssm:{Region}{path}/none"))).Message);
    }
}
