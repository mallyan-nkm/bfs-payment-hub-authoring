using System.Net;
using System.Text;
using TransactHub.Activities.Native;
using TransactHub.Ports;
using Xunit;

namespace TransactHub.Store.Tests;

/// <summary>
/// Native contract 2.1.0 on the bound ports (R-09-280, R-09-281; Issue I-383): a module sends a body of bytes with its content type on
/// a binding, with the credential the image keeps, and puts a file through a binding's transfer, the bank's file-transfer binding at the
/// folder the port's binding names. So a scheme's port on another wire (a signed XML envelope, a file) needs no case in the image. The
/// port, the endpoint, the token and the file are synthetic.
/// </summary>
public sealed class BoundPortsWireTests
{
    const string Tenant = "test-bank";
    const string Port = "p.port.test-wire.v1";
    static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
    static NativeAttempt Attempt => new("p.exchange.test-wire.v1", "1.0.0", new(), new(), Now, Tenant);

    /// <summary>The endpoint behind the binding: what it was sent, as the wire carried it.</summary>
    sealed class Endpoint : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Request, string? ContentType, byte[] Body)> Calls = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Calls.Add((request, request.Content?.Headers.ContentType?.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<ack status=\"ACCP\"/>")) { Headers = { ContentType = new("application/xml") } } };
        }
    }

    static readonly string Token = "synthetic-" + Guid.NewGuid().ToString("N");
    static readonly TransactHub.Security.SecretResolver Secrets = TransactHub.Security.SecretResolver.FromEnvironment(k => k == "TH_TEST_WIRE_TOKEN" ? Token : null);

    static PortBindings Bindings(string adapter = "gateway-soap")
    {
        var b = new PortBindings();
        b.Add($"""
            kind: port-bindings
            tenant: {Tenant}
            bindings:
              - port: {Port}
                adapter: {adapter}
                validFrom: 2026-11-01T00:00:00Z
                settings:
                  baseUrl: https://wire.example.invalid/
                  secretRef: env:TH_TEST_WIRE_TOKEN
                  submit.folder: partner.outFolder
                  submit.kind: p.test-wire.out
              - port: p.port.file-transfer.v1
                adapter: simulator
                validFrom: 2026-11-01T00:00:00Z
                settings:
                  partner.outFolder: upload/partner/out
            """);
        return b;
    }

    [Fact]
    public void A_module_sends_a_body_of_bytes_under_its_content_type_with_the_credential_the_image_keeps()
    {
        var endpoint = new Endpoint();
        var ports = new NativeBoundPorts(Bindings(), Secrets, _ => new HttpClient(endpoint));
        var binding = ports.For(Attempt, Port);
        var envelope = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?><Envelope><Body><Submit ref=\"REF-0001\"/></Body></Envelope>");
        var answer = ports.Send(Attempt, binding, new NativeHttpRequest("POST", "services/submit") { Content = new NativeContent("application/soap+xml; charset=utf-8", envelope) });
        Assert.True(answer.Success);
        Assert.Equal("application/xml", answer.ContentType);
        Assert.Contains("ACCP", answer.Text());
        var (req, type, body) = Assert.Single(endpoint.Calls);
        Assert.Equal("https://wire.example.invalid/services/submit", req.RequestUri!.ToString());
        Assert.Equal("application/soap+xml; charset=utf-8", type);
        Assert.Equal(envelope, body);
        Assert.Equal(("Bearer", Token), (req.Headers.Authorization!.Scheme, req.Headers.Authorization.Parameter));

        // One body or none; a content type that is not one is refused before anything is sent.
        Assert.Throws<NativePortRefused>(() => ports.Send(Attempt, binding, new NativeHttpRequest("POST", "x", Body: new System.Text.Json.Nodes.JsonObject()) { Content = new NativeContent("text/plain", [1]) }));
        Assert.Throws<NativePortRefused>(() => ports.Send(Attempt, binding, new NativeHttpRequest("POST", "x") { Content = new NativeContent("not a type", [1]) }));
        Assert.Single(endpoint.Calls);
        // A JSON body is sent as before (contract 2.0.0).
        ports.Send(Attempt, binding, new NativeHttpRequest("POST", "services/json", Body: new System.Text.Json.Nodes.JsonObject { ["ref"] = "REF-0002" }));
        Assert.Equal("application/json; charset=utf-8", endpoint.Calls[1].ContentType);
    }

    [Fact]
    public void A_module_puts_a_file_through_the_bindings_transfer_at_the_folder_the_binding_names()
    {
        var transfer = new SimulatedFileTransfer();
        var ports = new NativeBoundPorts(Bindings(PortFileTransfer.Name), Secrets, transferOf: _ => transfer);
        var binding = ports.For(Attempt, Port);
        var content = Encoding.UTF8.GetBytes("H,REF-0003\nT,1\n");
        var sent = ports.Put(Attempt, binding, new NativeFilePut("submit", "REF-0003.csv", content, "PAY-WIRE-1", "leg1"));
        Assert.Equal(("simulator", "partner/upload/partner/out", "REF-0003.csv", "p.test-wire.out", content.Length), (sent.Via, sent.Folder, sent.FileName, sent.Kind, sent.Bytes));
        Assert.Equal(content, transfer.Get("partner/upload/partner/out", "REF-0003.csv").GetAwaiter().GetResult());

        // A name that is a folder, an operation the binding names no folder for, and a binding of another adapter are refused.
        Assert.Throws<NativePortRefused>(() => ports.Put(Attempt, binding, new NativeFilePut("submit", "../REF.csv", content)));
        Assert.Throws<NativePortRefused>(() => ports.Put(Attempt, binding, new NativeFilePut("cancel", "REF-0004.csv", content)));
        var soap = new NativeBoundPorts(Bindings(), Secrets, transferOf: _ => transfer);
        var other = Assert.Throws<NativeFailure>(() => soap.Put(Attempt, soap.For(Attempt, Port), new NativeFilePut("submit", "REF-0005.csv", content)));
        Assert.Equal(("PortNotBound", false), (other.Name, other.Retryable));
        // A binding that is not the one in force is refused.
        var stale = binding with { ValidFrom = binding.ValidFrom.AddDays(-1) };
        Assert.Equal("PortNotBound", Assert.Throws<NativeFailure>(() => ports.Put(Attempt, stale, new NativeFilePut("submit", "REF-0006.csv", content))).Name);
    }
}
