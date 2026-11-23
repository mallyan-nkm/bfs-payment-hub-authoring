using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactHub.Security;

namespace TransactHub.Auth;

/// <summary>
/// Mutual TLS between runtime hosts, as an option (spec R-08-21, Issue I-121). A host serves https with its own certificate
/// and, when a client CA is configured, takes only callers whose client certificate that CA signed for client
/// authentication. The certificate then stands for the caller in place of a bearer token: its subject's common name, its DNS
/// names and its thumbprint become claims (<c>x509:cn</c>, <c>x509:dns</c>, <c>x509:thumbprint</c>), and the bank's role map
/// gives the roles per tenant from them exactly as it does from a token's claims (R-06-28). Every certificate and key is read
/// by reference, never from the configuration itself. Configuration, section <c>Tls</c>:
/// <c>CertificateRef</c> and <c>KeyRef</c> (the host's certificate and private key, PEM), <c>ClientCaRef</c> (the PEM of the
/// CA that signs the callers' certificates; without it the host serves https but asks for no client certificate).
/// </summary>
public sealed class RuntimeTls
{
    /// <summary>The authentication scheme of a client certificate.</summary>
    public const string Scheme = "th-client-certificate";
    /// <summary>Client authentication, the extended key usage a caller's certificate must carry.</summary>
    const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    public required X509Certificate2 Server { get; init; }
    public X509Certificate2? ClientCa { get; init; }

    /// <summary>The settings, or null when the host is not configured for TLS.</summary>
    public static async Task<RuntimeTls?> FromConfiguration(IConfiguration cfg, ISecretResolver secrets)
    {
        var certRef = cfg["Tls:CertificateRef"];
        if (string.IsNullOrEmpty(certRef)) return null;
        var keyRef = cfg["Tls:KeyRef"] ?? throw new InvalidOperationException("Tls:CertificateRef needs Tls:KeyRef: the host's private key, by reference (spec R-08-21)");
        var cert = (await secrets.Resolve(certRef)).Reveal();
        var key = (await secrets.Resolve(keyRef)).Reveal();
        // Loaded through PKCS#12 so the key is usable by the platform's TLS on every operating system.
        using var pem = X509Certificate2.CreateFromPem(cert, key);
        var server = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        X509Certificate2? ca = null;
        if (cfg["Tls:ClientCaRef"] is { Length: > 0 } caRef) ca = X509Certificate2.CreateFromPem((await secrets.Resolve(caRef)).Reveal());
        return new RuntimeTls { Server = server, ClientCa = ca };
    }

    /// <summary>Kestrel's https: the host's certificate and, with a client CA, a client certificate that CA signed.</summary>
    public void Apply(KestrelServerOptions k) => k.ConfigureHttpsDefaults(h =>
    {
        h.ServerCertificate = Server;
        if (ClientCa is null) return;
        h.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        h.ClientCertificateValidation = (c, _, _) => Trusted(c);
    });

    /// <summary>Whether the client CA signed this certificate, for client authentication, and it is in date.</summary>
    public bool Trusted(X509Certificate2 cert)
    {
        if (ClientCa is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ClientCa);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(ClientAuth));
        return chain.Build(cert);
    }

    /// <summary>The claims a trusted client certificate gives, before the role map.</summary>
    public static ClaimsIdentity IdentityOf(X509Certificate2 cert)
    {
        var id = new ClaimsIdentity(Scheme, "x509:cn", RuntimeAuth.RoleClaim);
        if (cert.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } cn)
        {
            id.AddClaim(new Claim("x509:cn", cn));
            id.AddClaim(new Claim("sub", cn));
        }
        foreach (var ext in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
            foreach (var dns in ext.EnumerateDnsNames()) id.AddClaim(new Claim("x509:dns", dns));
        id.AddClaim(new Claim("x509:thumbprint", cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)));
        return id;
    }
}

/// <summary>Authenticates a caller by its TLS client certificate (R-08-21), and gives it roles by the bank's role map.</summary>
public sealed class ClientCertificateHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, RuntimeTls tls, RoleMap map)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cert = Context.Connection.ClientCertificate ?? await Context.Connection.GetClientCertificateAsync();
        if (cert is null) return AuthenticateResult.NoResult();
        if (!tls.Trusted(cert)) return AuthenticateResult.Fail("The client certificate is not one the host's client CA signed for client authentication");
        var id = RuntimeTls.IdentityOf(cert);
        map.Apply(id);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), RuntimeTls.Scheme));
    }
}
