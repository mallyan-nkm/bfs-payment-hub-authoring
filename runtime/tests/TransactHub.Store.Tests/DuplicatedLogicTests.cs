using System.Text.Json.Nodes;
using TransactHub.Activities.Native;
using Xunit;
using Image = TransactHub.Activities.Platform.Posting;
using Module = TransactHub.Activities.Platform.Native;

namespace TransactHub.Store.Tests;

/// <summary>
/// Issue I-227, R-09-215: two pieces of logic are written twice, once in the runtime image and once in the platform's native module,
/// because a module may not reference the image (R-09-125). Until the posting moves into the module, each pair MUST give the same
/// answers on the same cases: the posting window gate, and the accounting template's reading of its parameters.
/// </summary>
public sealed class DuplicatedLogicTests
{
    static readonly string?[] Times = [null, "", " ", "13.45", "25:00", "9:5", "00:00", "06:00", "08:30", "12:00", "13:59", "14:00", "18:30", "22:00", "23:59", " 08:00 "];

    [Fact]
    public void The_posting_window_gate_of_the_image_and_of_the_platform_module_give_the_same_answer_on_every_case()
    {
        var day = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Unspecified);
        var cases = 0;
        foreach (var start in Times)
            foreach (var end in Times)
                for (var minute = 0; minute < 24 * 60; minute += 37)
                {
                    var now = day.AddMinutes(minute);
                    var a = Image.PostingWindow.Evaluate(start, end, now);
                    var b = Module.PostingWindow.Evaluate(start, end, now);
                    Assert.True(($"{a.State}", a.WindowId, a.OpenedAtLocal, a.NowLocal) == ($"{b.State}", b.WindowId, b.OpenedAtLocal, b.NowLocal), $"{start}-{end} at {now:HH:mm}: image {a}, module {b}");
                    cases++;
                }
        Assert.Equal(Times.Length * Times.Length * 39, cases);
    }

    static JsonObject Params(params (string k, string v)[] kv)
    {
        var o = new JsonObject();
        foreach (var (k, v) in kv) o[k] = v;
        return o;
    }

    [Fact]
    public void The_accounting_template_of_the_image_and_of_the_platform_module_read_the_same_values_and_refuse_the_same_gaps()
    {
        var full = Params(("template", "t1"), ("transactionCode", "PAY"), ("reversalTransactionCode", "REV"), ("chargeTransactionCode", "CHG"), ("vatTransactionCode", "VAT"),
            ("narration", "Payment"), ("chargeNarration", "Charge"), ("settlementAccount", "GL-S"), ("suspenseAccount", "GL-U"), ("chargeIncomeAccount", "GL-C"), ("vatAccount", "GL-V"));
        var cases = new List<JsonObject> { full, Params(("transactionCode", "PAY"), ("narration", "Payment"), ("settlementAccount", "GL-S")) };
        foreach (var k in new[] { "transactionCode", "narration", "settlementAccount", "template", "vatAccount" })
        {
            var c = (JsonObject)full.DeepClone();
            c.Remove(k);
            cases.Add(c);
        }
        foreach (var p in cases)
        {
            string Of(Func<object> read)
            {
                try { return read().ToString()!; }
                catch (NativeFailure e) { return $"refused {e.Name}: {e.Message}"; }
            }
            var image = Of(() => { var t = Image.AccountingTemplate.From(p); return (t.Template, t.TransactionCode, t.ReversalTransactionCode, t.ChargeTransactionCode, t.VatTransactionCode, t.Narration, t.ChargeNarration, t.SettlementAccount, t.SuspenseAccount, t.ChargeIncomeAccount, t.VatAccount); });
            var module = Of(() => { var t = Module.PostingTemplate.From(p); return (t.Template, t.TransactionCode, t.ReversalTransactionCode, t.ChargeTransactionCode, t.VatTransactionCode, t.Narration, t.ChargeNarration, t.SettlementAccount, t.SuspenseAccount, t.ChargeIncomeAccount, t.VatAccount); });
            Assert.Equal(image, module);
        }
    }
}
