using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using TransactHub.Activities.Platform.Posting;
using TransactHub.Ports;
using Xunit;
using Module = TransactHub.Activities.Platform.Native;

namespace TransactHub.Store.Tests;

/// <summary>What the platform module of a distribution gives the image's core-banking calls: its gate on the binding (R-09-264).</summary>
public static class ModuleGate
{
    /// <summary>The distribution's contributions as the native host reads them, with the platform module's posting window gate.</summary>
    public static readonly Func<string, object?> Contributions = k =>
        k == NativeContributions.BindingGate(CoreBankingResolver.Port) ? (NativeBindingGate)Module.PostingGate.Check : null;

    /// <summary><c>p.post.accounting.v1</c>, the platform module's, on the image's core banking gated by the module.</summary>
    public static INativeActivity PostAccounting(CoreBankingResolver r) => new Module.PostAccountingV1(new NativeCoreBanking(r, Contributions));
}

/// <summary>
/// Issue I-227, R-09-264 (in place of R-09-215's DuplicatedLogicTests): the accounting template's reading, <c>p.post.accounting.v1</c> and
/// the posting window gate were written twice, in the image and in the platform module, because a module may not reference the image.
/// Since native contract 2.0.0 they are the platform module's only: the image keeps no copy, and its core-banking calls ask the gate the
/// module contributed, refusing a binding with a posting window when no gate is contributed.
/// </summary>
public sealed class NoCopyInTheImageTests
{
    [Fact]
    public void The_image_keeps_no_copy_of_the_posting_window_gate_the_accounting_template_or_the_builtin_posting()
    {
        var image = typeof(CoreBankingResolver).Assembly;
        var names = image.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var copy in new[] { "PostingWindow", "PostingWindowState", "PostingWindowResult", "PostingGate", "AccountingTemplate", "PostingTemplate", "ChargeSlab", "PostAccounting" })
            Assert.DoesNotContain(copy, names);
        // The image's core-banking activities do not include the posting: the platform module serves it.
        var r = new CoreBankingResolver(new PortBindings());
        Assert.DoesNotContain(PlatformActivities.CoreBanking(r), a => a.Name.StartsWith("p.post.accounting.", StringComparison.Ordinal));
        Assert.Equal(["p.post.reverse.v1", "p.post.verify.v1", "p.reserve.funds.v1", "p.reserve.release.v1"], PlatformActivities.CoreBanking(r).Select(a => a.Name).Order(StringComparer.Ordinal));
        // The platform module carries them, and contributes its gate to the distribution.
        var module = typeof(Module.PostingGate).Assembly;
        Assert.Contains(module.GetTypes(), t => t.Name == "PostingWindow");
        var services = new Recorder();
        var served = new Module.PlatformNativeModule().Activities(services).Select(a => a.Name).ToList();
        Assert.Contains("p.post.accounting.v1", served);
        Assert.Contains("p.decide.posting-window.v1", served);
        Assert.IsType<NativeBindingGate>(services.Contributed[NativeContributions.BindingGate(CoreBankingResolver.Port)]);
        // No source of the image evaluates a window: the gate's settings are named once, for the refusal below.
        var src = Path.Combine(RuntimeRoot(), "src", "TransactHub.Activities.Platform");
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")))
            Assert.DoesNotContain("PostingWindow.Evaluate", File.ReadAllText(f));
    }

    static string RuntimeRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TransactHub.Runtime.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("runtime root");
    }

    static PortBindings Windowed()
    {
        var bindings = new PortBindings();
        bindings.Add("kind: port-bindings\ntenant: t\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n      currency: AED\n      postingWindowStart: \"07:00\"\n      postingWindowEnd: \"22:00\"\n");
        return bindings;
    }

    static readonly JsonObject Outward = (JsonObject)JsonNode.Parse("""
        { "header": { "paymentId": "P-W", "direction": "outward", "businessDate": "2026-11-24" },
          "payment": { "debtorAccount": { "id": "0010000000001" }, "instructedAmount": { "value": 10, "currency": "AED" } }, "batch": { "controlSum": 10 } }
        """)!;

    [Fact]
    public void The_images_core_banking_calls_ask_the_modules_gate_and_refuse_a_windowed_binding_when_none_is_contributed()
    {
        var r = new CoreBankingResolver(Windowed());
        var open = DateTimeOffset.Parse("2026-11-24T06:00:00Z");     // 10:00 in the UAE
        var closed = DateTimeOffset.Parse("2026-11-24T20:00:00Z");   // 00:00 in the UAE
        NativeAttempt At(DateTimeOffset now) => new("p.reserve.funds.v1", "1.0.0", Outward, new JsonObject(), now, "t");
        // Gated by the platform module's contribution: open, the balance is asked; closed, a retryable error and core banking is not called.
        Assert.Equal("reserved", new ReserveFunds(r, ModuleGate.Contributions).Run(At(open)).Outcome);
        var e = Assert.Throws<NativeFailure>(() => new ReserveFunds(r, ModuleGate.Contributions).Run(At(closed)));
        Assert.Equal(("TransientError", true), (e.Name, e.Retryable));
        // With no gate contributed (a distribution without the platform module), a windowed binding is never called ungated.
        var ungated = Assert.Throws<NativeFailure>(() => new ReserveFunds(r).Run(At(open)));
        Assert.Equal(("PortNotBound", false), (ungated.Name, ungated.Retryable));
        Assert.Contains("no module of the distribution gates it", ungated.Message);
        Assert.Throws<NativeFailure>(() => new NativeCoreBanking(r).For(At(open)));
        // A module's posting through the contract is gated the same way.
        Assert.Equal("TransientError", Assert.Throws<NativeFailure>(() => new NativeCoreBanking(r, ModuleGate.Contributions).For(At(closed))).Name);
        // A binding with no window is called with or without a gate.
        var plain = new PortBindings();
        plain.Add("kind: port-bindings\ntenant: t\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n      currency: AED\n");
        Assert.Equal("reserved", new ReserveFunds(new CoreBankingResolver(plain)).Run(At(closed)).Outcome);
    }

    /// <summary>A module's view of the host, recording what it contributes.</summary>
    sealed class Recorder : INativeServices
    {
        public readonly Dictionary<string, object> Contributed = new(StringComparer.Ordinal);
        public string? Distribution => null;
        public void KeepRendered(string sha256, string fileName, byte[] content) { }
        public void Contribute(string key, object value) => Contributed[key] = value;
        public object? Contribution(string key) => Contributed.GetValueOrDefault(key);
        public INativeCoreBanking CoreBanking { get; } = new NativeCoreBanking(new CoreBankingResolver(new PortBindings()));
        public INativePorts Ports { get; } = new NativePorts(new PortBindings(), new MemoryNativeFiles());
        public INativeBoundPorts BoundPorts { get; } = new NativeBoundPorts(new PortBindings());
        public INativeFiles Files { get; } = new MemoryNativeFiles();
    }
}
