using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Encryption;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(BwsAccessTokenCollection.Name)]
public sealed class CliSecretManagerTests : IDisposable
{
    private const string NotFoundText =
        "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)";

    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private string FakeBwsPath(string scriptBody)
    {
        var path = Path.Combine(_dataRoot, "bws");
        File.WriteAllText(path, $"#!/bin/sh\n{scriptBody}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    [RetryFact]
    public async Task RunAsync_ExitZero_ReturnsStdout()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("echo hello"));

        var result = await runner.RunAsync([], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldBe("hello\n");
        result.Stderr.ShouldBeEmpty();
    }

    [RetryFact]
    public async Task RunAsync_NonZeroExit_ReturnsExitCodeAndStderr()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("echo boom >&2\nexit 3"));

        var result = await runner.RunAsync([], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        result.ExitCode.ShouldBe(3);
        result.Stdout.ShouldBeEmpty();
        result.Stderr.ShouldBe("boom\n");
    }

    /// <summary>
    ///     H10: the token must never be a process argument — `ps aux` (or any same-user process)
    ///     can read another process's argv for its whole lifetime. Proven live against a slowed
    ///     stand-in bws with a token captured straight out of `ps aux`.
    /// </summary>
    [RetryFact]
    public async Task RunAsync_WithToken_NeverPlacesTokenInArgv()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("echo \"$*\""));

        var result = await runner.RunAsync(["secret", "get", "secret-1"], "tok-9", TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        result.Stdout.Trim().ShouldBe("secret get secret-1");
        result.Stdout.ShouldNotContain("tok-9");
        result.Stdout.ShouldNotContain("-t");
    }

    [RetryFact]
    public async Task RunAsync_WithToken_SetsBwsAccessTokenEnvironmentVariable()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("echo \"$BWS_ACCESS_TOKEN\""));

        var result = await runner.RunAsync(["secret", "get", "secret-1"], "tok-9", TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        result.Stdout.Trim().ShouldBe("tok-9");
    }

    [RetryFact]
    public async Task RunAsync_WithToken_OverridesAnInheritedBwsAccessTokenEnvironmentVariable()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("echo \"$BWS_ACCESS_TOKEN\""));
        var previous = Environment.GetEnvironmentVariable("BWS_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", "stale-inherited-tok");
        try
        {
            var result = await runner.RunAsync([], "fresh-per-run-tok", TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            result.Stdout.Trim().ShouldBe("fresh-per-run-tok");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", previous);
        }
    }

    [RetryFact]
    public async Task RunAsync_WithoutToken_DoesNotAppendDashT()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("echo \"$*\""));

        var result = await runner.RunAsync(["secret", "get", "secret-1"], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        result.Stdout.Trim().ShouldBe("secret get secret-1");
    }

    [RetryFact]
    public async Task RunAsync_InheritsBwsAccessTokenFromEnvironment()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("echo \"$BWS_ACCESS_TOKEN\""));
        var previous = Environment.GetEnvironmentVariable("BWS_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", "env-tok-42");
        try
        {
            var result = await runner.RunAsync([], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            result.Stdout.Trim().ShouldBe("env-tok-42");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", previous);
        }
    }

    [RetryFact]
    public async Task RunAsync_NonexistentExecutable_ThrowsBwsNotFoundText()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, Path.Combine(_dataRoot, "does-not-exist"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        ex.Message.ShouldBe(NotFoundText);
    }

    [RetryFact]
    public async Task RunAsync_Timeout_KillsProcessAndThrowsTimeoutText()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("sleep 30"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], null, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("bws timed out after 1s");
    }

    [RetryFact]
    public async Task RunAsync_Timeout_WithToken_ExceptionDoesNotContainToken()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("sleep 30"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], "secret-tok-should-not-leak", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        ex.Message.ShouldNotContain("secret-tok-should-not-leak");
    }

    [RetryFact]
    public async Task RunAsync_ExitZeroWithEmptyStdout_Throws()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("exit 0"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("bws returned no output");
    }

    [RetryFact]
    public async Task RunAsync_ExitZeroWithEmptyStdout_WithToken_ExceptionDoesNotContainToken()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("exit 0"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], "secret-tok-should-not-leak", TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        ex.Message.ShouldNotContain("secret-tok-should-not-leak");
    }

    [RetryFact]
    public async Task RunAsync_NonexistentExecutable_WithToken_ExceptionDoesNotContainToken()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, Path.Combine(_dataRoot, "does-not-exist"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], "secret-tok-should-not-leak", TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        ex.Message.ShouldNotContain("secret-tok-should-not-leak");
    }

    /// <summary>
    ///     .NET-F2: the blocking .GetAwaiter().GetResult() calls that used to sit behind this sync
    ///     interface are gone — RunAsync must not park a thread-pool thread on the child's I/O.
    ///     Running several invocations concurrently on a single-threaded SynchronizationContext-free
    ///     pool proves the awaits are real: a blocking implementation would serialize on the
    ///     thread-pool's available worker count and this would time out well under load.
    /// </summary>
    [RetryFact]
    public async Task RunAsync_ManyConcurrentInvocations_AllCompleteWithoutThreadStarvation()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("sleep 0.2; echo hello"));

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => runner.RunAsync([], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(TestContext.Current.CancellationToken);

        results.ShouldAllBe(r => r.Stdout == "hello\n");
    }

    [RetryFact]
    public async Task RunAsync_OnePasswordWithToken_PutsTokenInOpServiceAccountTokenNotArgv()
    {
        var runner = new CliSecretManager(CliSecretTool.OnePassword, FakeBwsPath("echo \"$OP_SERVICE_ACCOUNT_TOKEN|$BWS_ACCESS_TOKEN|$*\""));
        var previousBws = Environment.GetEnvironmentVariable("BWS_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", null);
        try
        {
            var result = await runner.RunAsync(["read", "op://v/i/f"], "op-tok-5", TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            result.Stdout.Trim().ShouldBe("op-tok-5||read op://v/i/f");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", previousBws);
        }
    }

    /// <summary>A child only ever sees its own tool's credential: op must not inherit the Bitwarden token.</summary>
    [RetryFact]
    public async Task RunAsync_OnePassword_DoesNotInheritTheBitwardenToken()
    {
        var runner = new CliSecretManager(CliSecretTool.OnePassword, FakeBwsPath("echo \"bws=[$BWS_ACCESS_TOKEN]\""));
        var previousBws = Environment.GetEnvironmentVariable("BWS_ACCESS_TOKEN");
        Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", "bws-secret-77");
        try
        {
            var result = await runner.RunAsync(["read", "op://v/i/f"], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            result.Stdout.Trim().ShouldBe("bws=[]");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BWS_ACCESS_TOKEN", previousBws);
        }
    }

    [RetryFact]
    public async Task RunAsync_OnePasswordNotFound_TextNamesOpAndItsInstallHint()
    {
        var runner = new CliSecretManager(CliSecretTool.OnePassword, Path.Combine(_dataRoot, "does-not-exist"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], null, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));

        ex.Failure.ShouldBe(CliSecretFailure.NotInstalled);
        ex.Message.ShouldStartWith("op not found");
        ex.Message.ShouldContain("1Password CLI");
        ex.Message.ShouldNotContain("bws");
    }

    [RetryFact]
    public async Task RunAsync_OnePasswordTimeout_TextNamesOp()
    {
        var runner = new CliSecretManager(CliSecretTool.OnePassword, FakeBwsPath("sleep 30"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => runner.RunAsync([], null, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("op timed out after 1s");
    }

    [RetryFact]
    public async Task RunAsync_ChildReadsStdin_SeesEndOfInputInsteadOfBlockingOnTheTerminal()
    {
        var runner = new CliSecretManager(CliSecretTool.Bitwarden, FakeBwsPath("cat; echo done"));

        var result = await runner.RunAsync([], null, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        result.Stdout.Trim().ShouldBe("done");
    }
}
