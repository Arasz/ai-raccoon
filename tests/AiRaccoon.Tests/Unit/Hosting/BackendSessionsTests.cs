using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     The proxy's half of auto-start: <see cref="BackendSessions" /> acquires the backend through
///     <see cref="BackendSessions.AcquireSharedAsync" /> with explicit probe/prover/launcher seams,
///     so the attach, start-on-port, proven-fallback and answered/unanswered-probe paths are pinned
///     without a real spawn and without depending on how the test host itself was launched. The
///     forwarder above it is covered by <c>ProxyForwardTests</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackendSessionsTests
{
    private const string AppHost = "/opt/ai-raccoon/ai-raccoon";
    private const string DotnetHost = "/usr/local/share/dotnet/dotnet";

    private static ServerConfig Config(int port, string dataRoot) =>
        new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User });

    private static BackendSessions Subject(IBackendLauncher launcher, string? processPath, ServerConfig config,
        IIdentityProver? prover = null, IServerProbe? probe = null, ILoggerFactory? loggerFactory = null,
        Func<string, bool>? fileExists = null, string? userProfileDirectory = null, string? pathVariable = null) =>
        new(launcher, prover ?? new FakeIdentityProver(), probe ?? new FakeServerProbe(ProbeVerdict.NotListening),
            new PlainHttpClientFactory(), loggerFactory ?? NullLoggerFactory.Instance, processPath, config,
            fileExists ?? (_ => true), userProfileDirectory, pathVariable);

    private static Task<BackendSessions.AcquireOutcome> AcquireAsync(IServerProbe probe, IIdentityProver prover,
        IBackendLauncher launcher, ServerConfig config, ILogger? logger = null, TimeSpan? fallbackIdle = null) =>
        BackendSessions.AcquireSharedAsync(probe, prover, launcher, AppHost, config, fallbackIdle,
            logger ?? new FakeLogger(), TestContext.Current.CancellationToken);

    // ── Acquire policy ──

    [Fact]
    public async Task AcquireShared_WithAProvenListener_AttachesWithoutConsultingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));
        var prover = new FakeIdentityProver();
        var config = Config(54240, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Answered), prover, launcher, config);

        outcome.Result.Url.ShouldBe("http://127.0.0.1:54240/mcp");
        outcome.Fallback.ShouldBeFalse();
        launcher.Calls.ShouldBe(0, "a proven listener is attached to, never probed-and-spawned again");
        prover.Calls.Count.ShouldBe(1);
        prover.Calls[0].ShouldBe(new Uri("http://127.0.0.1:54240/mcp"));
    }

    [Fact]
    public async Task AcquireShared_WithNoListener_StartsOnTheConfiguredPort()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54241/mcp", null));
        var prover = new FakeIdentityProver();
        var config = Config(54241, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.NotListening), prover, launcher, config);

        outcome.Result.Url.ShouldBe("http://127.0.0.1:54241/mcp");
        outcome.Fallback.ShouldBeFalse();
        launcher.AttachCalls.ShouldBe(1);
        launcher.PrivateCalls.ShouldBe(0);
        prover.Calls.Count.ShouldBe(1, "the listener that started on the configured port must prove before the token rides");
    }

    [Fact]
    public async Task AcquireShared_WithAnUnprovenListener_FallsBackPrivatelyWithAWarning()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54242/mcp", null),
            privateResult: new BackendResult("http://127.0.0.1:54298/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        prover.AnswerNext(null); // the fallback child proves under this root's key
        var logger = new FakeLogger();
        var config = Config(54242, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Answered), prover, launcher, config, logger);

        outcome.Result.Url.ShouldBe("http://127.0.0.1:54298/mcp");
        outcome.Fallback.ShouldBeTrue();
        launcher.AttachCalls.ShouldBe(0, "an unproven listener is never attached to");
        launcher.PrivateCalls.ShouldBe(1);
        prover.Calls[0].ShouldBe(new Uri("http://127.0.0.1:54242/mcp"),
            "the configured listener must be challenged before the fallback decision");
        prover.Calls[1].ShouldBe(new Uri("http://127.0.0.1:54298/mcp"), "the fallback child must prove too");
        launcher.PrivateArguments.ShouldContain("--port");
        launcher.PrivateArguments[Array.IndexOf(launcher.PrivateArguments, "--port") + 1].ShouldBe("0");
        var record = logger.Collector.GetSnapshot().Single(r => r.Id == 690);
        record.Message.ShouldContain("54242");
        record.Message.ShouldContain("stop the listener");
        record.Message.ShouldContain("BadSignature");
    }

    [Fact]
    public async Task HangingProbe_ChallengesThenFallsBack()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54243/mcp", null),
            privateResult: new BackendResult("http://127.0.0.1:54299/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.Timeout);
        prover.AnswerNext(null);
        var config = Config(54243, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Unanswered), prover, launcher, config);

        prover.Calls[0].ShouldBe(new Uri("http://127.0.0.1:54243/mcp"),
            "an unanswered probe is challenged before the fallback decision, never treated as 'nothing there'");
        prover.Calls[1].ShouldBe(new Uri("http://127.0.0.1:54299/mcp"));
        outcome.Fallback.ShouldBeTrue();
        launcher.PrivateCalls.ShouldBe(1);
        launcher.AttachCalls.ShouldBe(0,
            "an unanswered holder is not 'nothing listening' — the port must not be started on");
    }

    [Fact]
    public async Task AcquireShared_WhenTheFallbackChildDoesNotProve_HandsBackNoUrl()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54244/mcp", null),
            privateResult: new BackendResult("http://127.0.0.1:54297/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        var config = Config(54244, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Answered), prover, launcher, config);

        outcome.Result.Url.ShouldBeNull("a fallback child that does not prove must never be handed to the token-bearing caller");
        outcome.Fallback.ShouldBeTrue();
        prover.Calls[1].ShouldBe(new Uri("http://127.0.0.1:54297/mcp"),
            "the no-url verdict must come from the fallback child's own failed proof");
    }

    [Fact]
    public async Task AcquireShared_WhenTheStartedListenerCannotProve_FallsBackPrivately()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54245/mcp", null),
            privateResult: new BackendResult("http://127.0.0.1:54296/mcp", null));
        // The configured-port start hands back a URL, but a racer owns it: proof fails there, then
        // the fallback child proves.
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        prover.AnswerNext(null);
        var config = Config(54245, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.NotListening), prover, launcher, config);

        outcome.Fallback.ShouldBeTrue();
        launcher.AttachCalls.ShouldBe(1);
        launcher.PrivateCalls.ShouldBe(1);
        prover.Calls[0].ShouldBe(new Uri("http://127.0.0.1:54245/mcp"));
        prover.Calls[1].ShouldBe(new Uri("http://127.0.0.1:54296/mcp"));
        outcome.Result.Url.ShouldBe("http://127.0.0.1:54296/mcp");
    }

    [Fact]
    public async Task AcquireShared_PassesTheFallbackIdleBoundToThePrivateChild()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54246/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        prover.AnswerNext(null);
        var config = Config(54246, "/tmp/unused");

        await AcquireAsync(new FakeServerProbe(ProbeVerdict.Answered), prover, launcher, config,
            fallbackIdle: TimeSpan.FromMinutes(5));

        launcher.PrivateArguments.ShouldContain("--idle-timeout");
        launcher.PrivateArguments[Array.IndexOf(launcher.PrivateArguments, "--idle-timeout") + 1].ShouldBe("5m");
    }

    // ── Reopen: one private fallback per proxy ──

    private static Task<BackendSessions.AcquireOutcome> ReacquireAsync(Uri? privateBackend, IServerProbe probe,
        IIdentityProver prover, IBackendLauncher launcher, ServerConfig config) =>
        BackendSessions.ReuseOrAcquireAsync(privateBackend, probe, prover, launcher, AppHost, config, null,
            new FakeLogger(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reacquire_WhenThePrivateBackendStillProves_ReusesItWithoutProbingOrLaunching()
    {
        var privateBackend = new Uri("http://127.0.0.1:54290/mcp");
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54250/mcp", null),
            privateResult: new BackendResult("http://127.0.0.1:54291/mcp", null));
        var prover = new FakeIdentityProver();
        var probe = new FakeServerProbe(ProbeVerdict.Answered);

        var outcome = await ReacquireAsync(privateBackend, probe, prover, launcher, Config(54250, "/tmp/unused"));

        outcome.Result.Url.ShouldBe(privateBackend.ToString());
        outcome.Fallback.ShouldBeTrue("the reused backend is still this proxy's private fallback");
        launcher.PrivateCalls.ShouldBe(0, "a proven private child is reused, not started a second time");
        launcher.AttachCalls.ShouldBe(0);
        probe.Calls.ShouldBeEmpty("the configured port is not probed while the private child still proves");
        prover.Calls.ShouldBe([privateBackend], "only the private child is challenged, never the configured port");
    }

    [Fact]
    public async Task Reacquire_WhenThePrivateBackendNoLongerProves_FallsThroughToANormalAcquire()
    {
        var privateBackend = new Uri("http://127.0.0.1:54292/mcp");
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54251/mcp", null),
            privateResult: new BackendResult("http://127.0.0.1:54293/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.Timeout);
        prover.AnswerNext(IdentityProofFailure.BadSignature); // the squatter still holds the configured port
        prover.AnswerNext(null); // the new private child proves
        var probe = new FakeServerProbe(ProbeVerdict.Answered);

        var outcome = await ReacquireAsync(privateBackend, probe, prover, launcher, Config(54251, "/tmp/unused"));

        prover.Calls[0].ShouldBe(privateBackend, "the old private child is re-proved before anything else");
        prover.Calls[1].ShouldBe(new Uri("http://127.0.0.1:54251/mcp"));
        launcher.PrivateCalls.ShouldBe(1, "a dead private child is replaced by a fresh one");
        outcome.Result.Url.ShouldBe("http://127.0.0.1:54293/mcp");
        outcome.Fallback.ShouldBeTrue();
    }

    [Fact]
    public async Task Reacquire_WithNoPrivateBackend_IsTheNormalAcquire()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));
        var prover = new FakeIdentityProver();
        var probe = new FakeServerProbe(ProbeVerdict.Answered);

        var outcome = await ReacquireAsync(null, probe, prover, launcher, Config(54252, "/tmp/unused"));

        outcome.Result.Url.ShouldBe("http://127.0.0.1:54252/mcp");
        outcome.Fallback.ShouldBeFalse();
        probe.Calls.ShouldBe([54252]);
        prover.Calls.ShouldBe([new Uri("http://127.0.0.1:54252/mcp")]);
        launcher.Calls.ShouldBe(0);
    }

    // ── OpenAsync around the policy ──

    [Fact]
    public async Task OpenAsync_WhenTheProcessIsTheDotnetHost_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54230/mcp", null));
        await using var sessions = Subject(launcher, DotnetHost, Config(54230, "/tmp/unused"));

        var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
            sessions.OpenAsync(null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("dotnet host");
        error.Message.ShouldContain("serve --port 54230");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task OpenAsync_WhenTheProcessPathIsUnknown_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54231/mcp", null));
        await using var sessions = Subject(launcher, null, Config(54231, "/tmp/unused"));

        var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
            sessions.OpenAsync(null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("unknown");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task OpenAsync_WhenNoBackendComesUp_ThrowsUnavailable_WithTheConfiguredEndpointAndStderr()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-no-url", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "ai-raccoon: could not decrypt the bank"));
            await using var sessions = Subject(launcher, AppHost, Config(54232, dataRoot));

            var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
                sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            error.Message.ShouldContain("http://127.0.0.1:54232/mcp");
            error.Message.ShouldContain("could not decrypt the bank");
            launcher.FileName.ShouldBe(AppHost);
            launcher.AttachCalls.ShouldBe(1);
            launcher.PrivateCalls.ShouldBe(0);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task OpenAsync_WhenTheListenerProves_AttachesAndDoesNotSpawn()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-proven", TestContext.Current.CancellationToken);
        try
        {
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));
            await using var sessions = Subject(launcher, AppHost, Config(1, dataRoot),
                new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.Answered));

            // No HTTP session can open against port 1; what this pins is that the acquire went
            // through the attach arm (no launcher call at all) before the session attempt.
            await Should.ThrowAsync<BackendUnavailableException>(() =>
                sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            sessions.Url.ShouldBe("http://127.0.0.1:1/mcp");
            launcher.Calls.ShouldBe(0);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task OpenAsync_WhenTheLauncherThrowsBackendStart_WrapsAsUnavailable()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-start-throws", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendStartException("could not start it", new InvalidOperationException()));
            await using var sessions = Subject(launcher, AppHost, Config(54233, dataRoot));

            var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
                sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            error.Message.ShouldContain("could not start it");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task OpenAsync_WhenTheDataRootHoldsNoToken_ThrowsUnavailable_NamingTheTokenPath()
    {
        var dataRoot = TestData.CreateTempRoot("backend-sessions-no-token");
        try
        {
            // The bank must exist (F39) while the token deliberately does not — that absence is
            // the verdict this test pins. The listener proves, so the acquire succeeds.
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));
            await using var sessions = Subject(launcher, AppHost, Config(1, dataRoot),
                new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.Answered));

            var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
                sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            error.Message.ShouldContain(McpTokenFile.FileName);
            // ADR-0107 PC.4: identity is already proven (FakeIdentityProver defaults to proven) by
            // the time the token is read, so "another data root" is never a live possibility here.
            error.Message.ShouldNotContain("another data root");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     ADR-0107 PC.4: <see cref="McpTokenFile.RefusalReason" /> (set by <see cref="McpTokenFile.Read" />
    ///     when the state directory is not owner-only) is the real, actionable cause — it must be
    ///     surfaced instead of the generic "holds no token" line.
    /// </summary>
    [Fact]
    public async Task OpenAsync_WhenTheStateDirectoryIsNotOwnerOnly_SurfacesTheRefusalReason()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("UnixFileMode is POSIX-only");
            return;
        }

        var dataRoot = TestData.CreateTempRoot("backend-sessions-not-owner-only");
        try
        {
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            File.SetUnixFileMode(dataRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));
            await using var sessions = Subject(launcher, AppHost, Config(1, dataRoot),
                new FakeIdentityProver(), new FakeServerProbe(ProbeVerdict.Answered));

            var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
                sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            error.Message.ShouldContain("chmod 700");
        }
        finally
        {
            File.SetUnixFileMode(dataRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    // ── Executable fallback: the own process path was deleted by `dotnet tool update` (ADR-0116) ──

    private static (ILoggerFactory Factory, FakeLoggerProvider Provider) FallbackLogging()
    {
        var provider = new FakeLoggerProvider();
        return (LoggerFactory.Create(builder => builder.AddProvider(provider)), provider);
    }

    [Fact]
    public async Task OpenAsync_WhenTheOwnExecutableExists_SpawnsItUnchanged_WithoutLogging()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-fallback-unchanged", TestContext.Current.CancellationToken);
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "boom"));
        var (loggerFactory, logs) = FallbackLogging();
        try
        {
            await using var sessions = Subject(launcher, AppHost, Config(54280, dataRoot), loggerFactory: loggerFactory);

            await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            launcher.FileName.ShouldBe(AppHost);
            logs.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 693);
        }
        finally
        {
            loggerFactory.Dispose();
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task OpenAsync_WhenTheOwnExecutableIsMissingButTheGlobalToolShimExists_SpawnsTheShim()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-fallback-shim", TestContext.Current.CancellationToken);
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "boom"));
        var shim = BackendLaunchArguments.GlobalToolShimPath("/home/rafal")!;
        var (loggerFactory, logs) = FallbackLogging();
        try
        {
            await using var sessions = Subject(launcher, AppHost, Config(54281, dataRoot), loggerFactory: loggerFactory,
                fileExists: path => path == shim, userProfileDirectory: "/home/rafal");

            await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            launcher.FileName.ShouldBe(shim);
            var record = logs.Collector.GetSnapshot().Single(r => r.Id.Id == 693);
            record.Message.ShouldContain(AppHost);
            record.Message.ShouldContain(shim);
        }
        finally
        {
            loggerFactory.Dispose();
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task OpenAsync_WhenTheOwnExecutableIsMissingAndNoShimButFoundOnPath_SpawnsThePathHit()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-fallback-path", TestContext.Current.CancellationToken);
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "boom"));
        var pathHit = Path.Combine("/usr/local/bin", BackendLaunchArguments.ExecutableFileName);
        var (loggerFactory, logs) = FallbackLogging();
        try
        {
            await using var sessions = Subject(launcher, AppHost, Config(54282, dataRoot), loggerFactory: loggerFactory,
                fileExists: path => path == pathHit, userProfileDirectory: "/home/rafal",
                pathVariable: string.Join(Path.PathSeparator, "/usr/bin", "/usr/local/bin"));

            await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            launcher.FileName.ShouldBe(pathHit);
            logs.Collector.GetSnapshot().Single(r => r.Id.Id == 693).Message.ShouldContain(AppHost);
        }
        finally
        {
            loggerFactory.Dispose();
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task OpenAsync_WhenTheOwnExecutableIsMissingWithNoFallback_KeepsTheOwnPath_WithoutLogging()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-fallback-none", TestContext.Current.CancellationToken);
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "boom"));
        var (loggerFactory, logs) = FallbackLogging();
        try
        {
            await using var sessions = Subject(launcher, AppHost, Config(54283, dataRoot), loggerFactory: loggerFactory,
                fileExists: _ => false, userProfileDirectory: "/home/rafal", pathVariable: "/usr/bin");

            await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            launcher.FileName.ShouldBe(AppHost, "no fallback was found, so today's refusal path is unchanged");
            logs.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 693);
        }
        finally
        {
            loggerFactory.Dispose();
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    // ── Proof before every token-bearing request ──

    /// <summary>
    ///     F3 ordering: the acquire proves before the MCP session (the first token-bearing
    ///     request), and the dispose stop proves before /shutdown. The shared event log holds the
    ///     prover's calls and the HTTP requests in one sequence, so the order is a fact, not an
    ///     inference from the code.
    /// </summary>
    [Fact]
    public async Task ProveIsRequired_BeforeEveryTokenBearingRequest()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-ordering", TestContext.Current.CancellationToken);
        try
        {
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var log = new List<string>();
            var prover = new RecordingProver(log, IdentityProofFailure.BadSignature);
            prover.AnswerNext(null); // the fallback child proves
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54260/mcp", null));
            var config = Config(54260, dataRoot);
            await using var sessions = new BackendSessions(launcher, prover,
                new FakeServerProbe(ProbeVerdict.Answered), new RecordingHttpClientFactory(log),
                NullLoggerFactory.Instance, AppHost, config, _ => true, null, null);

            // The session cannot open against the unreachable fake URL; the acquire has already run.
            await Should.ThrowAsync<BackendUnavailableException>(() =>
                sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            var fallbackProof = log.IndexOf("prove http://127.0.0.1:54260/mcp");
            var firstMcp = log.FindIndex(entry => entry.StartsWith("http POST /mcp", StringComparison.Ordinal));
            fallbackProof.ShouldBeGreaterThanOrEqualTo(0, $"no proof was attempted; log: {string.Join(", ", log)}");
            firstMcp.ShouldBeGreaterThan(fallbackProof,
                $"an MCP request rode before the fallback child proved; log: {string.Join(", ", log)}");

            // Hostile half: a listener that no longer proves gets no shutdown request at all.
            prover.AnswerNext(IdentityProofFailure.BadSignature);
            await sessions.DisposeAsync();
            log.ShouldNotContain(entry => entry.Contains("/shutdown", StringComparison.Ordinal));
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     The positive control for the ordering gate: with a listener that still proves, the
    ///     dispose stop does send /shutdown — and only after its proof.
    /// </summary>
    [Fact]
    public async Task DisposeStop_WithAProvenBackend_SendsTheShutdownAfterTheProof()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-ordering-control", TestContext.Current.CancellationToken);
        try
        {
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var log = new List<string>();
            var prover = new RecordingProver(log, IdentityProofFailure.BadSignature);
            prover.AnswerNext(null); // the fallback child proves at acquire
            prover.AnswerNext(null); // and again at stop
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54261/mcp", null));
            await using var sessions = new BackendSessions(launcher, prover,
                new FakeServerProbe(ProbeVerdict.Answered), new RecordingHttpClientFactory(log),
                NullLoggerFactory.Instance, AppHost, Config(54261, dataRoot), _ => true, null, null);

            try
            {
                await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            }
            catch (BackendUnavailableException)
            {
                // The fake URL refuses the session; the acquire and the private-backend record stand.
            }

            await sessions.DisposeAsync();

            var stopProof = log.LastIndexOf("prove http://127.0.0.1:54261/mcp");
            var shutdown = log.FindIndex(entry => entry.Contains("/shutdown", StringComparison.Ordinal));
            shutdown.ShouldBeGreaterThanOrEqualTo(0, $"no shutdown request was sent; log: {string.Join(", ", log)}");
            stopProof.ShouldBeGreaterThanOrEqualTo(0);
            stopProof.ShouldBeLessThan(shutdown, $"the token rode before the stop proof; log: {string.Join(", ", log)}");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task OpenAsync_Twice_AfterAFallback_ReusesTheOneChildAndStopsItOnce()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-reopen", TestContext.Current.CancellationToken);
        try
        {
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var log = new List<string>();
            // The squatter on the configured port never proves; the private child always does.
            var prover = new EndpointProver(log, new Uri("http://127.0.0.1:54262/mcp"));
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54262/mcp", null),
                privateResult: new BackendResult("http://127.0.0.1:54295/mcp", null));
            await using var sessions = new BackendSessions(launcher, prover,
                new FakeServerProbe(ProbeVerdict.Answered), new RecordingHttpClientFactory(log),
                NullLoggerFactory.Instance, AppHost, Config(54262, dataRoot), _ => true, null, null);

            for (var open = 0; open < 2; open++)
            {
                // The fake URL refuses the session; the acquire and the private-backend record stand.
                await Should.ThrowAsync<BackendUnavailableException>(() =>
                    sessions.OpenAsync(null, TestContext.Current.CancellationToken));
            }

            await sessions.DisposeAsync();

            launcher.PrivateCalls.ShouldBe(1, "a reopen reuses the proven private child");
            log.Count(entry => entry.Contains("/shutdown", StringComparison.Ordinal)).ShouldBe(1,
                $"the reused child is recorded once, so it is stopped once; log: {string.Join(", ", log)}");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>One shared log for the prover's calls and the HTTP requests, so ordering is observable.</summary>
    private sealed class RecordingProver(List<string> log, IdentityProofFailure? failure) : IIdentityProver
    {
        private readonly Queue<IdentityProofFailure?> _pending = new([failure]);
        private IdentityProofFailure? _last;

        public void AnswerNext(IdentityProofFailure? next) => _pending.Enqueue(next);

        public Task<IdentityProofFailure?> ProveAsync(Uri endpoint, CancellationToken ctx)
        {
            log.Add($"prove {endpoint}");
            if (_pending.Count > 0)
            {
                _last = _pending.Dequeue();
            }

            return Task.FromResult(_last);
        }
    }

    /// <summary>Logs every proof like <see cref="RecordingProver" />; only the squatted endpoint fails.</summary>
    private sealed class EndpointProver(List<string> log, Uri squatted) : IIdentityProver
    {
        public Task<IdentityProofFailure?> ProveAsync(Uri endpoint, CancellationToken ctx)
        {
            log.Add($"prove {endpoint}");
            return Task.FromResult<IdentityProofFailure?>(endpoint == squatted ? IdentityProofFailure.BadSignature : null);
        }
    }

    private sealed class RecordingHttpClientFactory(List<string> log) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RecordingHandler(log));
    }

    /// <summary>Records every request; a /shutdown is accepted, everything else is refused so the
    /// session attempt and the post-shutdown port poll both settle immediately.</summary>
    private sealed class RecordingHandler(List<string> log) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            log.Add($"http {request.Method} {path}");
            if (path.EndsWith("/shutdown", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }

            throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
        }
    }

    private sealed class FakeBackendLauncher : IBackendLauncher
    {
        private readonly Exception? _throws;
        private readonly BackendResult _result;
        private readonly BackendResult? _privateResult;

        public FakeBackendLauncher(BackendResult result, BackendResult? privateResult = null)
        {
            _result = result;
            _privateResult = privateResult;
        }

        public FakeBackendLauncher(Exception throws) => _throws = throws;

        public int Calls { get; private set; }

        public int PrivateCalls { get; private set; }

        public int AttachCalls { get; private set; }

        public string? FileName { get; private set; }

        public string[] PrivateArguments { get; private set; } = [];

        public Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Calls++;
            PrivateCalls++;
            FileName = fileName;
            PrivateArguments = [.. arguments];
            var result = _privateResult ?? _result;
            return _throws is null ? Task.FromResult(result) : Task.FromException<BackendResult>(_throws);
        }

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Calls++;
            AttachCalls++;
            FileName = fileName;
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
        }
    }
}
