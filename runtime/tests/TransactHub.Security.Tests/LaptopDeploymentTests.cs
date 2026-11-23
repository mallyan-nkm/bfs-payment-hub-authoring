using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TransactHub.Security.Tests;

/// <summary>
/// The laptop deployment (deploy/laptop, spec R-09-77), read as files: every image pinned by digest or built here (th-laptop-*),
/// every final image an unprivileged user with a health check, no secret value in the compose file or the images (a password or
/// a client secret only from the environment, a key only from a volume), every port on 127.0.0.1, and the development identity
/// provider refused in Production. The stack itself is brought up by deploy/laptop/test/insert-only.mts.
/// </summary>
public sealed class LaptopDeploymentTests
{
    static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "deploy", "laptop")) && Directory.Exists(Path.Combine(d.FullName, "runtime"))) return d.FullName;
        throw new DirectoryNotFoundException("the repository with deploy/laptop");
    }

    static readonly string Laptop = Path.Combine(Root(), "deploy", "laptop");
    static string Compose => File.ReadAllText(Path.Combine(Laptop, "compose.yaml"));
    static string Dockerfile => File.ReadAllText(Path.Combine(Laptop, "Dockerfile"));

    [Fact]
    public void Every_image_is_pinned_by_digest_or_built_here()
    {
        var images = Regex.Matches(Compose, @"^\s+image:\s*(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(images);
        Assert.All(images, i => Assert.True(i.Contains("@sha256:") || i.StartsWith("th-laptop-"), $"{i} is neither pinned by digest nor one of the laptop's own images"));
        var bases = Regex.Matches(Dockerfile, @"^ARG \w+_IMAGE=(\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(4, bases.Count);
        Assert.All(bases, b => Assert.Matches(@"@sha256:[0-9a-f]{64}$", b));
    }

    [Fact]
    public void Every_final_image_runs_as_an_unprivileged_user_with_a_health_check()
    {
        foreach (var target in new[] { "studio", "runtime", "temporal" })
        {
            var start = Dockerfile.IndexOf($" AS {target}\n", StringComparison.Ordinal);
            Assert.True(start > 0, $"no target {target}");
            var next = Dockerfile.IndexOf("\nFROM ", start, StringComparison.Ordinal);
            var stage = next < 0 ? Dockerfile[start..] : Dockerfile[start..next];
            Assert.Contains("USER app", stage);
            Assert.Contains("HEALTHCHECK", stage);
            Assert.DoesNotContain("USER root", stage);
        }
    }

    [Fact]
    public void No_secret_value_is_in_the_compose_file_or_an_image_and_every_port_is_on_loopback()
    {
        foreach (Match m in Regex.Matches(Compose, @"^\s+([A-Za-z0-9_]*(PASSWORD|SECRET|TOKEN)[A-Za-z0-9_]*):\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (m.Groups[1].Value.EndsWith("_ROOTS") || m.Groups[1].Value.EndsWith("_URL")) continue; // where secrets are read from, or a token endpoint: not a secret
            var v = m.Groups[3].Value.Trim().Trim('"');
            Assert.True(v.Length == 0 || v.StartsWith("${") || v.StartsWith("env:") || v.StartsWith("file:"), $"{m.Groups[1].Value} holds a value, not a reference or the environment: {v}");
        }
        foreach (Match m in Regex.Matches(Compose, @"Password=([^;""]*)"))
            Assert.StartsWith("${", m.Groups[1].Value);
        foreach (Match m in Regex.Matches(Compose, @"ports:\s*\[(.*?)\]"))
            Assert.All(m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries), p => Assert.StartsWith("\"127.0.0.1:", p.Trim()));
        var ignore = File.ReadAllText(Path.Combine(Laptop, "Dockerfile.dockerignore"));
        foreach (var never in new[] { "**/.env", "**/*.pem", "**/*.key", ".git" }) Assert.Contains(never, ignore.Split('\n').Select(l => l.Trim()));
        Assert.DoesNotMatch(@"(?im)^\s*(ENV|ARG)\s+\S*(PASSWORD|SECRET|TOKEN)\S*=", Dockerfile);
    }

    [Fact]
    public void The_development_identity_provider_is_refused_in_Production()
    {
        var project = Path.Combine(Root(), "runtime", "tools", "TransactHub.DevIdentity");
        var build = Process.Start(new ProcessStartInfo("dotnet", ["build", project, "--nologo", "-v", "q"]) { RedirectStandardOutput = true })!;
        var built = build.StandardOutput.ReadToEnd();
        Assert.True(build.WaitForExit(300000) && build.ExitCode == 0, built);
        var dll = Path.Combine(project, "bin", "Debug", "net10.0", "TransactHub.DevIdentity.dll");
        Assert.True(File.Exists(dll), dll);
        var psi = new ProcessStartInfo("dotnet", [dll, "serve"]) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60000));
        Assert.Equal(2, p.ExitCode);
        Assert.Contains("refused in Production", err);
    }
}
