using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Tests;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BwsProcessRunnerTests : IDisposable
{
    private const string NotFoundText =
        "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private string FakeBwsPath(string scriptBody)
    {
        var path = Path.Combine(_dataRoot, "bws");
        File.WriteAllText(path, "#!/bin/sh\n" + scriptBody + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [Fact]
    public void Run_ExitZero_ReturnsStdout()
    {
        var runner = new BwsProcessRunner(FakeBwsPath("echo hello"));

        var result = runner.Run([], null, TimeSpan.FromSeconds(15));

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("hello\n");
        result.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public void Run_NonZeroExit_ReturnsExitCodeAndStderr()
    {
        var runner = new BwsProcessRunner(FakeBwsPath("echo boom >&2\nexit 3"));

        var result = runner.Run([], null, TimeSpan.FromSeconds(15));

        result.ExitCode.ShouldBe(3);
        result.Stdout.ShouldBeEmpty();
        result.Stderr.ShouldBe("boom\n");
    }

    [Fact]
    public void Run_PassesArgsAndAppendsTokenAsDashT()
    {
        var runner = new BwsProcessRunner(FakeBwsPath("echo \"$*\""));

        var result = runner.Run(["secret", "get", "secret-1"], "tok-9", TimeSpan.FromSeconds(15));

        result.Stdout.Trim().ShouldBe("secret get secret-1 -t tok-9");
    }

    [Fact]
    public void Run_WithoutToken_DoesNotAppendDashT()
    {
        var runner = new BwsProcessRunner(FakeBwsPath("echo \"$*\""));

        var result = runner.Run(["secret", "get", "secret-1"], null, TimeSpan.FromSeconds(15));

        result.Stdout.Trim().ShouldBe("secret get secret-1");
    }

    [Fact]
    public void Run_InheritsBwsAccessTokenFromEnvironment()
    {
        var runner = new BwsProcessRunner(FakeBwsPath("echo \"$BWS_ACCESS_TOKEN\""));
        var previous = Environment.GetEnvironmentVariable("BWS_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", "env-tok-42");
        try
        {
            var result = runner.Run([], null, TimeSpan.FromSeconds(15));

            result.Stdout.Trim().ShouldBe("env-tok-42");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", previous);
        }
    }

    [Fact]
    public void Run_NonexistentExecutable_ThrowsBwsNotFoundText()
    {
        var runner = new BwsProcessRunner(Path.Combine(_dataRoot, "does-not-exist"));

        var ex = Should.Throw<BwsInvocationException>(() => runner.Run([], null, TimeSpan.FromSeconds(15)));

        ex.Message.ShouldBe(NotFoundText);
    }

    [Fact]
    public void Run_Timeout_KillsProcessAndThrowsTimeoutText()
    {
        var runner = new BwsProcessRunner(FakeBwsPath("sleep 30"));

        var ex = Should.Throw<BwsInvocationException>(() => runner.Run([], null, TimeSpan.FromSeconds(1)));

        ex.Message.ShouldBe("bws timed out after 1s");
    }

    [Fact]
    public void Run_ExitZeroWithEmptyStdout_Throws()
    {
        var runner = new BwsProcessRunner(FakeBwsPath("exit 0"));

        var ex = Should.Throw<BwsInvocationException>(() => runner.Run([], null, TimeSpan.FromSeconds(15)));

        ex.Message.ShouldBe("bws returned no output");
    }
}
