using System.Text;
using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Wps.Native;
using Xunit;

namespace TransactHub.Activities.Wps.Tests;

/// <summary>
/// R-09-263 (Issue I-237): the labour ministry's register is WPS's port, so its adapters are the WPS module's, built on the native
/// contract's bound ports (R-09-261). Here the bound ports are the contract's interface only, answered by a fake that records what the
/// module asks: the module builds the request, the image (the fake) keeps the endpoint and the credential. Employer ids are synthetic.
/// </summary>
public sealed class LabourMinistryTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T08:00:00Z");

    sealed class FakePorts(NativeBinding? binding, Func<NativeHttpRequest, NativeHttpResponse>? answer = null) : INativeBoundPorts
    {
        public readonly List<(NativeBinding Binding, NativeHttpRequest Request)> Sent = [];

        public NativeBinding For(NativeAttempt a, string port)
        {
            if (string.IsNullOrEmpty(a.Tenant)) throw new NativeFailure("NoTenant", "no tenant", false);
            return binding is { } b && b.Port == port && b.Tenant == a.Tenant ? b : throw new NativeFailure("PortNotBound", $"{a.Tenant} binds no {port}", false);
        }

        public NativeHttpResponse Send(NativeAttempt a, NativeBinding b, NativeHttpRequest request)
        {
            Sent.Add((b, request));
            return answer!(request);
        }

        // The register's adapters put no file (contract 2.1.0's file put is for a port on a file wire, R-09-281).
        public NativeFileSent Put(NativeAttempt a, NativeBinding b, NativeFilePut file) => throw new InvalidOperationException("The register puts no file");
    }

    static NativeBinding Binding(string adapter, Dictionary<string, string> settings) =>
        new("test-bank", EmployerRegisterAdapters.Port, adapter, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), "1.0.0", settings);

    static NativeAttempt Attempt(string employer, string? tenant = "test-bank") => new("uae.wps.enrich.employer-status.v1", "1.1.0",
        new JsonObject { ["ext"] = new JsonObject { ["uae"] = new JsonObject { ["wps"] = new JsonObject { ["employerId"] = employer } } } }, new JsonObject(), Now, tenant, "batch");

    static NativeHttpResponse Json(int status, string body) => new(status, "application/json", Encoding.UTF8.GetBytes(body));

    [Fact]
    public void The_simulator_adapter_is_the_modules_own_on_the_binding_in_force()
    {
        var ports = new FakePorts(Binding("simulator", new() { ["suspended"] = "0000001234567, 0000001234560", ["notRegistered"] = "0000009999999" }));
        var act = new EnrichEmployerStatus(ports);
        Assert.Equal(("found", "suspended"), (act.Run(Attempt("0000001234567")).Outcome, (string?)act.Run(Attempt("0000001234567")).Writes["ext.uae.wps.employerStatus"]));
        Assert.Equal("suspended", (string?)act.Run(Attempt("0000001234560")).Writes["ext.uae.wps.employerStatus"]);
        Assert.Equal(("found", "active"), (act.Run(Attempt("0000001234568")).Outcome, (string?)act.Run(Attempt("0000001234568")).Writes["ext.uae.wps.employerStatus"]));
        Assert.Equal("not-found", act.Run(Attempt("0000009999999")).Outcome);
        Assert.Empty(ports.Sent);
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => act.Run(Attempt("0000001234567", "other-bank"))).Name);
        Assert.Equal("NoTenant", Assert.Throws<NativeFailure>(() => act.Run(Attempt("0000001234567", null))).Name);
        Assert.Equal("BusinessReject", Assert.Throws<NativeFailure>(() => act.Run(Attempt(""))).Name);
    }

    [Fact]
    public void The_gateway_adapter_builds_its_request_and_leaves_the_endpoint_and_the_credential_to_the_image()
    {
        var ports = new FakePorts(Binding("gateway-rest", new() { ["baseUrl"] = "https://labour-register.test.invalid/", ["statusPath"] = "/api/establishments/status", ["secretRef"] = "env:TH_TEST_LM_TOKEN", ["channel"] = "BANK-CHANNEL" }),
            r => ((JsonObject)r.Body!)["employerId"]!.ToString() switch
            {
                "0000001234567" => Json(200, """{ "status": "Blocked" }"""),
                "0000001234568" => Json(200, """{ "status": "REGISTERED" }"""),
                "0000001234569" => Json(400, "{}"),
                _ => Json(404, ""),
            });
        var act = new EnrichEmployerStatus(ports);
        Assert.Equal("suspended", (string?)act.Run(Attempt("0000001234567")).Writes["ext.uae.wps.employerStatus"]);
        Assert.Equal("active", (string?)act.Run(Attempt("0000001234568")).Writes["ext.uae.wps.employerStatus"]);
        Assert.Equal(("not-found", "unknown"), (act.Run(Attempt("0000009999999")).Outcome, (string?)act.Run(Attempt("0000009999999")).Writes["ext.uae.wps.employerStatus"]));
        // An answer the register gives that is neither a status nor a 404 is retried by the step's policy.
        var e = Assert.Throws<NativeFailure>(() => act.Run(Attempt("0000001234569")));
        Assert.Equal(("TransientError", true), (e.Name, e.Retryable));
        var (b, req) = ports.Sent[0];
        Assert.Equal(("gateway-rest", "POST", "/api/establishments/status"), (b.Adapter, req.Method, req.Path));
        Assert.Equal("""{"employerId":"0000001234567"}""", req.Body!.ToJsonString());
        Assert.Equal("0000001234567", req.Headers!["X-Ref"]);
        Assert.Equal("BANK-CHANNEL", req.Headers["X-Channel"]);
        // The module names no host and sets no credential: the image applies them on the binding in force.
        Assert.DoesNotContain(req.Headers.Keys, k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("://", req.Path);
    }

    [Fact]
    public void A_binding_the_module_has_no_adapter_for_or_one_missing_a_setting_is_a_business_rejection()
    {
        Assert.Equal("BusinessReject", Assert.Throws<NativeFailure>(() => new EnrichEmployerStatus(new FakePorts(Binding("soap", new()))).Run(Attempt("0000001234567"))).Name);
        var noPath = new FakePorts(Binding("gateway-rest", new() { ["baseUrl"] = "https://labour-register.test.invalid/" }), _ => Json(200, "{}"));
        var e = Assert.Throws<NativeFailure>(() => new EnrichEmployerStatus(noPath).Run(Attempt("0000001234567")));
        Assert.Equal("BusinessReject", e.Name);
        Assert.Contains("'statusPath'", e.Message);
        Assert.Empty(noPath.Sent);
    }

    [Fact]
    public void The_modules_entry_point_builds_the_register_activity_on_the_bound_ports_it_is_given()
    {
        var module = new WpsNativeModule();
        var services = new Services(new FakePorts(Binding("simulator", new() { ["suspended"] = "0000001234567" })));
        var act = module.Activities(services).Single(a => a.Name == "uae.wps.enrich.employer-status.v1");
        Assert.Equal("suspended", (string?)act.Run(Attempt("0000001234567")).Writes["ext.uae.wps.employerStatus"]);
    }

    sealed class Services(INativeBoundPorts bound) : INativeServices
    {
        public string? Distribution => null;
        public void KeepRendered(string sha256, string fileName, byte[] content) { }
        public void Contribute(string key, object value) { }
        public object? Contribution(string key) => null;
        public INativeCoreBanking CoreBanking => null!;
        public INativePorts Ports => null!;
        public INativeBoundPorts BoundPorts => bound;
        public INativeFiles Files => null!;
    }
}
