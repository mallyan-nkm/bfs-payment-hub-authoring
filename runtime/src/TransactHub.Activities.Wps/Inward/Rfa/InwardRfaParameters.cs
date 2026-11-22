// The part of the TransactHub source's InwardRfaParameters (666f1ee, Inward/Rfa/InwardRfaParameters.cs) that the RFA file
// name policy needs: the head-office routing code rule. The rest of that class reads the source's wps.ParamConfiguration
// table; on the runtime those values are the activity's parameters, set in the bank's configuration.
namespace TransactHub.Activities.Wps.Inward.Rfa;

public static class InwardRfaParameters
{
    public const int MinHeadRoutingCodeLength = 4;
    public const int MaxHeadRoutingCodeLength = 20;

    public static bool IsValidHeadRoutingCode(string? value) =>
        value is { Length: >= MinHeadRoutingCodeLength and <= MaxHeadRoutingCodeLength } && value.All(char.IsAsciiLetterOrDigit);
}
