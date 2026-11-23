using System.Text.Json.Nodes;

namespace TransactHub.Native.Tests;

/// <summary>
/// Spec R-09-132: the payment an attempt is for is the view's <c>header.paymentId</c>, or, for an activity that does not read the
/// header (a map such as <c>uae.wps.map.sif-out.v1</c> reads the batch and its items only), the attempt's own <c>paymentId</c>, which
/// the worker sends beside the view. Without it a runtime's native host refused every such attempt as <c>native.NotPinned</c>, and
/// the laptop's salary files were never written (Issue I-285).
/// </summary>
public sealed class AttemptPaymentTests
{
    [Fact]
    public void The_payment_is_the_views_header_or_else_the_attempts_own()
    {
        var withHeader = new JsonObject { ["paymentId"] = "PAY-BODY", ["view"] = new JsonObject { ["header"] = new JsonObject { ["paymentId"] = "PAY-VIEW" } } };
        Assert.Equal("PAY-VIEW", AttemptRouting.PaymentOf(withHeader));
        var batchOnly = new JsonObject { ["paymentId"] = "PAY-BODY", ["view"] = new JsonObject { ["batch"] = new JsonObject { ["id"] = "B1" } } };
        Assert.Equal("PAY-BODY", AttemptRouting.PaymentOf(batchOnly));
        Assert.Null(AttemptRouting.PaymentOf(new JsonObject { ["view"] = new JsonObject() }));
    }
}
