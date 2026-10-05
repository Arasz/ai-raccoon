using System.Net;
using System.Net.Sockets;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using NSubstitute;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     The proxy's half of auto-start: <see cref="BackendSessions" /> acquires the backend through
///     <see cref="BackendSessions.AcquireSharedAsync" /> with explicit probe/prover/launcher seams,
///     so proven attach, configured-port start and unproven refusal are pinned
///     without a real spawn and without depending on how the test host itself was launched. The
///     forwarder above it is covered by <c>ProxyForwardTests</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackendSessionsTests
{
    private const string AppHost = "/opt/ai-raccoon/ai-raccoon";
    private const string DotnetHost = "/usr/local/share/dotnet/dotnet";

    private static ServerConfig Config(int port, string dataRoot) => new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User });

    private static BackendSessions Subject(IBackendLauncher launcher, string? processPath, ServerConfig config,
        IIdentityProver? prover = null, IServerProbe? probe = null, ILoggerFactory? loggerFactory = null,
        Func<string, bool>? fileExists = null, string? userProfileDirectory = null, string? pathVariable = null,
        string? currentProcessPath = null) =>
        new(launcher, prover ?? new FakeIdentityProver(), probe ?? new FakeServerProbe(ProbeVerdict.NotListening),
            new PlainHttpClientFactory(), loggerFactory ?? NullLoggerFactory.Instance, processPath, config,
            fileExists ?? (_ => true), userProfileDirectory, pathVariable, currentProcessPath ?? processPath);

    private static Task<BackendSessions.AcquireOutcome> AcquireAsync(IServerProbe probe, IIdentityProver prover,
        IBackendLauncher launcher, ServerConfig config, ILogger? logger = null) =>
        BackendSessions.AcquireSharedAsync(probe, prover, launcher, AppHost, config,
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
        launcher.AttachCalls.ShouldBe(1);

        prover.Calls.Count.ShouldBe(1, "the listener that started on the configured port must prove before the token rides");
    }

    [Theory]
    [InlineData(ProbeVerdict.Answered, IdentityProofFailure.BadSignature)]
    [InlineData(ProbeVerdict.Answered, IdentityProofFailure.NoKey)]
    [InlineData(ProbeVerdict.Answered, IdentityProofFailure.RootMismatch)]
    [InlineData(ProbeVerdict.Answered, IdentityProofFailure.Malformed)]
    [InlineData(ProbeVerdict.Answered, IdentityProofFailure.NonSuccessStatus)]
    [InlineData(ProbeVerdict.Answered, IdentityProofFailure.Timeout)]
    [InlineData(ProbeVerdict.Unanswered, IdentityProofFailure.Timeout)]
    [InlineData(ProbeVerdict.Unanswered, IdentityProofFailure.BadSignature)]
    public async Task AcquireShared_WithAnUnprovenListener_RefusesWithoutStarting(ProbeVerdict verdict, IdentityProofFailure failure)
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54242/mcp", null));
        var prover = new FakeIdentityProver(failure);
        var outcome = await AcquireAsync(new FakeServerProbe(verdict), prover, launcher, Config(54242, "/tmp/unused"));
        outcome.Result.Url.ShouldBeNull();
        outcome.ProofFailure.ShouldBe(failure);
        launcher.Calls.ShouldBe(0);
        prover.Calls.ShouldBe([new Uri("http://127.0.0.1:54242/mcp")]);
    }

    [Fact]
    public async Task AcquireShared_WhenTheStartedListenerCannotProve_RefusesWithoutSecondStart()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54245/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.NotListening), prover, launcher, Config(54245, "/tmp/unused"));
        outcome.Result.Url.ShouldBeNull();
        outcome.ProofFailure.ShouldBe(IdentityProofFailure.BadSignature);
        launcher.Calls.ShouldBe(1);
        prover.Calls.ShouldBe([new Uri("http://127.0.0.1:54245/mcp")]);
    }

    [Fact]
    public async Task AcquireShared_WithAnInconclusiveProbeAndValidProof_Attaches()
    {
        var launcher = new FakeBackendLauncher(new BackendResult(null, null));
        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Unanswered), new FakeIdentityProver(), launcher, Config(54242, "/tmp/unused"));
        outcome.Result.Url.ShouldBe("http://127.0.0.1:54242/mcp");
        launcher.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcquireShared_WhenCallerCancelsAtACompletedBoundary_PropagatesBeforeFurtherWork(bool cancelAfterProbe)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = Substitute.For<IServerProbe>();
        var prover = Substitute.For<IIdentityProver>();
        var proofCalls = 0;
        probe.ProbeAsync(54242, caller.Token).Returns(_ =>
        {
            if (cancelAfterProbe) { caller.Cancel(); }
            return Task.FromResult(ProbeVerdict.Answered);
        });
        prover.ProveAsync(ServerProbe.EndpointFor(54242), caller.Token).Returns(_ =>
        {
            proofCalls++;
            caller.Cancel();
            return Task.FromResult<IdentityProofFailure?>(null);
        });
        var launcher = new FakeBackendLauncher(new BackendResult(null, null));
        await Should.ThrowAsync<OperationCanceledException>(() => BackendSessions.AcquireSharedAsync(
            probe, prover, launcher, AppHost, Config(54242, "/tmp/unused"), NullLogger.Instance, caller.Token));
        proofCalls.ShouldBe(cancelAfterProbe ? 0 : 1);
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireShared_WhenLauncherCancelsAndReturnsNoUrl_PropagatesCancellation()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var launcher = Substitute.For<IBackendLauncher>();
        launcher.AcquireAsync(54242, AppHost, Arg.Any<IReadOnlyList<string>>(), caller.Token).Returns(_ =>
        {
            caller.Cancel();
            return Task.FromResult(new BackendResult(null, null));
        });
        var prover = new FakeIdentityProver();
        await Should.ThrowAsync<OperationCanceledException>(() => BackendSessions.AcquireSharedAsync(
            new FakeServerProbe(ProbeVerdict.NotListening), prover, launcher, AppHost, Config(54242, "/tmp/unused"),
            NullLogger.Instance, caller.Token));
        prover.Calls.ShouldBeEmpty();
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
            error.Code.ShouldBe(ErrorCode.Server.NoToken);
            launcher.Calls.ShouldBe(0);
            File.Exists(new McpTokenFile(dataRoot).Path).ShouldBeFalse();
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

    /// <summary>
    ///     The fallback's whole remit is the process's own executable, deleted mid-run by `dotnet
    ///     tool update` (ADR-0116). An explicitly named executable that is gone is a launch
    ///     failure, not a licence to spawn whatever `ai-raccoon` the shim or PATH happens to offer.
    /// </summary>
    [Fact]
    public async Task OpenAsync_WhenAnExplicitlyNamedExecutableIsGone_NeverSwapsItForAnotherBinary()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("backend-sessions-explicit-gone", TestContext.Current.CancellationToken);
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "boom"));
        var explicitPath = Path.Combine(dataRoot, "no-such-ai-raccoon");
        var shim = BackendLaunchArguments.GlobalToolShimPath("/home/rafal")!;
        var (loggerFactory, logs) = FallbackLogging();
        try
        {
            // currentProcessPath stands in for the real Environment.ProcessPath: the explicit path
            // above is deliberately not it, which is exactly what must never be swapped.
            await using var sessions = Subject(launcher, explicitPath, Config(54284, dataRoot), loggerFactory: loggerFactory,
                fileExists: path => path == shim, userProfileDirectory: "/home/rafal", currentProcessPath: AppHost);

            await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            launcher.FileName.ShouldBe(explicitPath,
                "an explicitly named executable must reach the launcher unchanged, never the shim or a PATH hit");
            logs.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 693);
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

    [Fact]
    public async Task OpenAsync_WhenReopenedAfterProofFault_RefusesWithoutAnotherSessionOrStart()
    {
        var root = await TestData.CreateTempRootWithBankAsync("proxy-reopen-proof", TestContext.Current.CancellationToken);
        try
        {
            await new McpTokenFile(root).EnsureAsync(TestContext.Current.CancellationToken);
            var log = new List<string>();
            var prover = new RecordingProver(log, null);
            prover.AnswerNext(IdentityProofFailure.BadSignature);
            var launcher = new FakeBackendLauncher(new BackendResult(null, null));
            await using var sessions = new BackendSessions(launcher, prover, new FakeServerProbe(ProbeVerdict.Answered),
                new RecordingHttpClientFactory(log), NullLoggerFactory.Instance, AppHost, Config(54262, root),
                _ => true, null, null, AppHost);
            var initial = await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));
            initial.Code.ShouldBe(ErrorCode.Server.SessionRefused);
            var requestCount = log.Count(entry => entry.StartsWith("http", StringComparison.Ordinal));
            requestCount.ShouldBeGreaterThan(0);
            log[0].ShouldBe("prove http://127.0.0.1:54262/mcp");
            var refusal = await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));
            refusal.Code.ShouldBe(ErrorCode.Server.Unproven);
            launcher.Calls.ShouldBe(0);
            log.Count(entry => entry.StartsWith("http", StringComparison.Ordinal)).ShouldBe(requestCount);
            await sessions.DisposeAsync();
            log.ShouldNotContain(entry => entry.Contains("/shutdown", StringComparison.Ordinal));
        }
        finally
        {
            TestData.DeleteTempRoot(root);
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

        public Task<IdentityProofFailure?> ProveAsync(Uri endpoint, CancellationToken ctx)
        {
            log.Add($"prove {endpoint}");
            if (_pending.Count > 0)
            {
                _last = _pending.Dequeue();
            }

            return Task.FromResult(_last);
        }

        public async Task<ProvenChannel> ProveChannelAsync(Uri endpoint, CancellationToken ctx) =>
            await ProveAsync(endpoint, ctx) is { } failure
                ? ProvenChannel.NotProven(failure)
                : ProvenChannel.Proven(new HttpClient(new RecordingHandler(log, "channel")));

        public void AnswerNext(IdentityProofFailure? next) => _pending.Enqueue(next);
    }

    private sealed class RecordingHttpClientFactory(List<string> log) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RecordingHandler(log, "http"));
    }

    /// <summary>Records every request under <paramref name="route" /> — "http" for the factory's
    /// shared client, "channel" for a proven channel — so a test sees which one carried it; a
    /// /shutdown is accepted, everything else is refused so the session attempt and the
    /// post-shutdown port poll both settle immediately.</summary>
    private sealed class RecordingHandler(List<string> log, string route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            log.Add($"{route} {request.Method} {path}");
            if (path.EndsWith("/shutdown", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }

            throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused));
        }
    }

    private sealed class FakeBackendLauncher : IBackendLauncher
    {
        private readonly BackendResult _result;
        private readonly Exception? _throws;

        public FakeBackendLauncher(BackendResult result)
        {
            _result = result;
        }

        public FakeBackendLauncher(Exception throws) => _throws = throws;

        public int Calls { get; private set; }

        public int AttachCalls { get; private set; }

        public string? FileName { get; private set; }

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Calls++;
            AttachCalls++;
            FileName = fileName;
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
        }
    }
}
