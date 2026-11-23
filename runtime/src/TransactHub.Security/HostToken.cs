using System.Security.Cryptography;
using System.Text;

namespace TransactHub.Security;

/// <summary>
/// The token the engine hosts and their supervisor take from their callers (spec R-08-22, Issue I-121). The harness hosts serve
/// only the machine they run on; on top of that every call carries <c>Authorization: Bearer &lt;token&gt;</c>. The token is read
/// by reference from <c>TH_ENGINE_HOST_TOKEN</c> (env:, file:, vault:, akv:, aws-sm:, aws-ssm:); a supervisor without one makes
/// a new token for each run and gives it to the hosts it starts, by reference, so a host is never called without one.
/// </summary>
public static class HostToken
{
    public const string Setting = "TH_ENGINE_HOST_TOKEN";

    /// <summary>The configured token, or null when none is set.</summary>
    public static async Task<string?> FromEnvironment(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var reference = env(Setting);
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var value = (await SecretResolver.FromEnvironment(env).Resolve(reference)).Reveal();
        if (value.Length < 16) throw new InvalidOperationException($"{Setting} names a token of fewer than 16 characters");
        return value;
    }

    /// <summary>A new token, for the hosts one supervisor starts.</summary>
    public static string New() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether an Authorization header carries the token, compared in constant time.</summary>
    public static bool Accepts(string expected, string? authorization)
    {
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var got = Encoding.UTF8.GetBytes(authorization[7..].Trim());
        var want = Encoding.UTF8.GetBytes(expected);
        return got.Length == want.Length && CryptographicOperations.FixedTimeEquals(got, want);
    }
}
