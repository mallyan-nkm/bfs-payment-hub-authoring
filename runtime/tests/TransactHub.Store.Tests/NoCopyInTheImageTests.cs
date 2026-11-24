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

    /// <summary>The image's core banking as the platform module of a distribution gets it, gated by the module.</summary>
    public static INativeCoreBanking CoreBanking(CoreBankingResolver r) => new NativeCoreBanking(r, Contributions);

    /// <summary><c>p.post.accounting.v1</c>, the platform module's, on the image's core banking gated by the module.</summary>
    public static INativeActivity PostAccounting(CoreBankingResolver r) => new Module.PostAccountingV1(CoreBanking(r));

    /// <summary><c>p.reserve.funds.v1</c>, the platform module's since contract 2.1.0 (R-09-278).</summary>
    public static INativeActivity Reserve(CoreBankingResolver r) => new Module.ReserveFunds(CoreBanking(r));

    /// <summary><c>p.post.reverse.v1</c>, the platform module's since contract 2.1.0 (R-09-278).</summary>
    public static INativeActivity Reverse(CoreBankingResolver r) => new Module.ReversePosting(CoreBanking(r));
}

/// <summary>
/// Issue I-227, R-09-264 (in place of R-09-215's DuplicatedLogicTests): the accounting template's reading, <c>p.post.accounting.v1</c> and
/// the posting window gate were written twice, in the image and in the platform module, because a module may not reference the image.
/// Since native contract 2.0.0 they are the platform module's only, and since 2.1.0 (R-09-278, R-09-279, Issue I-382) so are the
/// reservation, its release, the reversal and the check of a posting: the image keeps the core-banking adapters and no core-banking
/// activity, names no setting of a posting window, and calls core banking only through the gate a module contributed.
/// </summary>
public sealed class NoCopyInTheImageTests
{
    static readonly string[] CoreBankingActivities = ["p.post.accounting.v1", "p.post.accounting.v2", "p.post.reverse.v1", "p.post.verify.v1", "p.reserve.funds.v1", "p.reserve.release.v1"];

    [Fact]
    public void The_image_keeps_no_core_banking_activity_no_copy_of_the_gate_and_names_no_setting_of_a_posting_window()
    {
        var image = typeof(CoreBankingResolver).Assembly;
        var names = image.GetTypes().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var copy in new[] { "PostingWindow", "PostingWindowState", "PostingWindowResult", "PostingGate", "AccountingTemplate", "PostingTemplate", "ChargeSlab", "PostAccounting",
                     "ReserveFunds", "ReleaseReservation", "ReversePosting", "VerifyPosting", "CoreBankingActivity", "PlatformActivities" })
            Assert.DoesNotContain(copy, names);
        // No activity of the image answers core banking: every INativeActivity it holds is a hold or a production adapter.
        var activities = image.GetTypes().Where(t => typeof(INativeActivity).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface).ToList();
        Assert.DoesNotContain(activities, t => t.Namespace == typeof(CoreBankingResolver).Namespace);
        // The platform module carries them, and contributes its gate to the distribution.
        var module = typeof(Module.PostingGate).Assembly;
        Assert.Contains(module.GetTypes(), t => t.Name == "PostingWindow");
        var services = new Recorder();
        var served = new Module.PlatformNativeModule().Activities(services).Select(a => a.Name).ToList();
        foreach (var a in CoreBankingActivities.Append("p.decide.posting-window.v1")) Assert.Contains(a, served);
        Assert.IsType<NativeBindingGate>(services.Contributed[NativeContributions.BindingGate(CoreBankingResolver.Port)]);
        // No source of the image evaluates a window or names its settings (R-09-279), and none names a core-banking activity of the platform.
        var src = Path.Combine(RuntimeRoot(), "src");
        foreach (var project in new[] { "TransactHub.Activities.Platform", "TransactHub.Native", "TransactHub.Ports", "TransactHub.Store", "TransactHub.Operations" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(src, project), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")))
            {
                var text = File.ReadAllText(f);
                Assert.DoesNotContain("PostingWindow.Evaluate", text);
                Assert.False(text.Contains("postingWindowStart", StringComparison.Ordinal) || text.Contains("postingWindowEnd", StringComparison.Ordinal), $"{f} names a setting of the posting window");
                // The operations console's ledger view reads the postings' facts by their activity (Ledger.Templates): it answers no activity.
                if (project != "TransactHub.Operations")
                    foreach (var a in CoreBankingActivities)
                        Assert.False(text.Contains($"\"{a}\"", StringComparison.Ordinal), $"{f} names {a}, which is the platform module's");
            }
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
    public void The_images_core_banking_asks_the_modules_gate_and_calls_nothing_when_no_gate_is_contributed()
    {
        var r = new CoreBankingResolver(Windowed());
        var open = DateTimeOffset.Parse("2026-11-24T06:00:00Z");     // 10:00 in the UAE
        var closed = DateTimeOffset.Parse("2026-11-24T20:00:00Z");   // 00:00 in the UAE
        NativeAttempt At(DateTimeOffset now) => new("p.reserve.funds.v1", "1.1.0", Outward, new JsonObject(), now, "t");
        // Gated by the platform module's contribution: open, the balance is asked; closed, a retryable error and core banking is not called.
        Assert.Equal("reserved", ModuleGate.Reserve(r).Run(At(open)).Outcome);
        var e = Assert.Throws<NativeFailure>(() => ModuleGate.Reserve(r).Run(At(closed)));
        Assert.Equal(("TransientError", true), (e.Name, e.Retryable));
        // A module's posting through the contract is gated the same way.
        Assert.Equal("TransientError", Assert.Throws<NativeFailure>(() => ModuleGate.CoreBanking(r).For(At(closed))).Name);
        // With no gate contributed (a table without the platform module), core banking is not called, whatever the binding names (R-09-279).
        var plain = new PortBindings();
        plain.Add("kind: port-bindings\ntenant: t\nbindings:\n  - port: p.port.core-banking.v1\n    adapter: simulator\n    validFrom: 2026-01-01T00:00:00Z\n    settings:\n      currency: AED\n");
        foreach (var bindings in new[] { Windowed(), plain })
        {
            var ungated = Assert.Throws<NativeFailure>(() => new Module.ReserveFunds(new NativeCoreBanking(new CoreBankingResolver(bindings))).Run(At(open)));
            Assert.Equal(("PortNotBound", false), (ungated.Name, ungated.Retryable));
            Assert.Contains("No module of the distribution gates p.port.core-banking.v1", ungated.Message);
        }
        // A binding with no window is called at any hour through the gate.
        Assert.Equal("reserved", ModuleGate.Reserve(new CoreBankingResolver(plain)).Run(At(closed)).Outcome);
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
