using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TransactHub.Security.Tests;

// Mutual TLS between runtime hosts, as an option (R-08-21, Issue I-121). A dev CA is made here, at test time: the native host's
// certificate and a worker's client certificate, both signed by it, and a stranger's from another CA. Every certificate and
// key reaches the host by reference (env: through its configuration here). The host serves https with its own certificate,
// takes only a client certificate its client CA signed for client authentication, and gives the caller roles by the bank's role
// map from the certificate's name, per tenant, exactly as from a token.
public sealed class NativeHostMutualTlsTests
{
    const string RoleMap = """
        { "kind": "runtime-role-map", "grants": [
          { "claim": "x509:cn", "value": "worker-example-bank", "roles": ["native-caller"], "tenants": ["example-bank"] },
          { "claim": "x509:cn", "value": "reader-example-bank", "roles": [] } ] }
        """;

    sealed class DevCa : IDisposable
    {
        public readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public readonly X509Certificate2 Cert;
        public DevCa(string name)
        {
            var req = new CertificateRequest($"CN={name}", Key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            Cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        }

        /// <summary>A certificate this CA signs: for the host (server authentication, 127.0.0.1) or a caller (client authentication).</summary>
        public (X509Certificate2 cert, ECDsa key) Issue(string cn, bool server)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], true));
            if (server)
            {
                var san = new SubjectAlternativeNameBuilder();
                san.AddIpAddress(IPAddress.Loopback);
                san.AddDnsName("localhost");
                req.CertificateExtensions.Add(san.Build());
            }
            var serial = RandomNumberGenerator.GetBytes(12);
            var cert = req.Create(Cert, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), serial);
            return (cert.CopyWithPrivateKey(key), key);
        }

        public void Dispose() => Key.Dispose();
    }

    static string Pem(X509Certificate2 c) => c.ExportCertificatePem();
    static string KeyPem(ECDsa k) => k.ExportPkcs8PrivateKeyPem();

    sealed class Host(Dictionary<string, string> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b)
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static object Attempt(string tenant) => new { activity = "uae.wps.validate.cbx-file.v1", release = "1.0.0", view = new { }, @params = new { }, now = "2026-11-23T08:00:00Z", tenant };

    static HttpClient Client(string url, X509Certificate2 trust, X509Certificate2? clientCert)
    {
        var h = new HttpClientHandler();
        if (clientCert is not null) h.ClientCertificates.Add(X509CertificateLoader.LoadPkcs12(clientCert.Export(X509ContentType.Pkcs12), null));
        // The worker checks the host's certificate against the bank's CA, not the machine's store.
        h.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
        {
            if (cert is null) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trust);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        };
        return new HttpClient(h) { BaseAddress = new Uri(url) };
    }

    [Fact]
    public async Task With_mutual_TLS_only_a_client_certificate_of_the_bank_s_CA_is_taken_and_its_role_is_per_tenant()
    {
        using var ca = new DevCa("TransactHub test CA");
        using var other = new DevCa("Another test CA");
        var (server, serverKey) = ca.Issue("native-host", server: true);
        var (worker, _) = ca.Issue("worker-example-bank", server: false);
        var (reader, _) = ca.Issue("reader-example-bank", server: false);
        var (stranger, _) = other.Issue("worker-example-bank", server: false);
        var port = FreePort();
        var url = $"https://127.0.0.1:{port}";
        await using var host = new Host(new()
        {
            ["TH_NATIVE_URLS"] = url,
            ["Tls:CertificateRef"] = "env:TH_TEST_TLS_CERT", ["TH_TEST_TLS_CERT"] = Pem(server),
            ["Tls:KeyRef"] = "env:TH_TEST_TLS_KEY", ["TH_TEST_TLS_KEY"] = KeyPem(serverKey),
            ["Tls:ClientCaRef"] = "env:TH_TEST_TLS_CA", ["TH_TEST_TLS_CA"] = Pem(ca.Cert),
            ["Auth:RoleMap"] = RoleMap,
        });
        host.UseKestrel(k => k.Listen(IPAddress.Loopback, port, l => l.UseHttps()));
        host.StartServer();

        // The bank's worker, by its certificate: served in its own bank, refused in another bank before any activity runs.
        using var w = Client(url, ca.Cert, worker);
        var served = await w.PostAsJsonAsync("/native", Attempt("example-bank"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.NotEqual("NotAuthorised", (string?)(await served.Content.ReadFromJsonAsync<JsonObject>())!["name"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.PostAsJsonAsync("/native", Attempt("other-bank"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.GetAsync("/files")).StatusCode);
        // A certificate of the CA whose name the role map gives no native-caller.
        using var r = Client(url, ca.Cert, reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.PostAsJsonAsync("/native", Attempt("example-bank"))).StatusCode);
        // No certificate, or one another CA signed with the same name: the TLS handshake is refused.
        using var none = Client(url, ca.Cert, null);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => none.PostAsJsonAsync("/native", Attempt("example-bank")));
        using var s = Client(url, ca.Cert, stranger);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => s.PostAsJsonAsync("/native", Attempt("example-bank")));
        // The worker refuses a host its CA did not sign.
        using var wrongTrust = Client(url, other.Cert, worker);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => wrongTrust.GetAsync("/health"));
    }

    [Fact]
    public void A_certificate_needs_its_key_by_reference()
    {
        using var host = new Host(new() { ["TH_NATIVE_URLS"] = "https://127.0.0.1:7443", ["Tls:CertificateRef"] = "env:TH_TEST_NONE" });
        Assert.Contains("Tls:KeyRef", Assert.ThrowsAny<Exception>(() => host.CreateClient()).ToString());
    }
}
