// The part of the TransactHub source's InwardRriParameters (666f1ee, Inward/Rri/InwardRriParameters.cs) that the RRI parser
// needs: the file name's agent code. On the runtime its value is the activity's parameter, set in the bank's configuration.
namespace TransactHub.Activities.Wps.Inward.Rri;

public static class InwardRriParameters
{
    public const string KeyFileNameAgentCode = "RriFileNameAgentCode";
    public const int FileNameAgentCodeLength = 3;
}
