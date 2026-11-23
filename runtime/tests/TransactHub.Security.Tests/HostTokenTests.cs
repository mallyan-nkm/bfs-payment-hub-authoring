namespace TransactHub.Security.Tests;

// The engine hosts and their supervisor take their callers' token (R-08-22, Issue I-121): read by reference, compared in
// constant time, and refused when it is missing, wrong or too short. The supervisor makes one for its hosts when none is set.
public sealed class HostTokenTests
{
    [Fact]
    public async Task The_token_is_read_by_reference_and_only_the_same_token_is_taken()
    {
        var env = new Dictionary<string, string?> { ["TH_ENGINE_HOST_TOKEN"] = "env:TH_TEST_HOST_TOKEN", ["TH_TEST_HOST_TOKEN"] = "synthetic-host-token-0123456789" };
        var token = await HostToken.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal("synthetic-host-token-0123456789", token);
        Assert.True(HostToken.Accepts(token!, "Bearer synthetic-host-token-0123456789"));
        Assert.False(HostToken.Accepts(token!, "Bearer synthetic-host-token-012345678"));
        Assert.False(HostToken.Accepts(token!, "synthetic-host-token-0123456789"));
        Assert.False(HostToken.Accepts(token!, null));
        Assert.False(HostToken.Accepts(token!, "Basic c3ludGhldGlj"));
    }

    [Fact]
    public async Task No_setting_is_no_token_a_short_one_is_refused_and_a_new_one_is_long_and_fresh()
    {
        Assert.Null(await HostToken.FromEnvironment(_ => null));
        var env = new Dictionary<string, string?> { ["TH_ENGINE_HOST_TOKEN"] = "env:SHORT", ["SHORT"] = "short" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => HostToken.FromEnvironment(k => env.GetValueOrDefault(k)));
        var a = HostToken.New();
        Assert.True(a.Length >= 40);
        Assert.NotEqual(a, HostToken.New());
    }
}
