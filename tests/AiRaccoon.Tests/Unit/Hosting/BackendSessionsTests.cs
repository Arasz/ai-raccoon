using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
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
        IIdentityProver? prover = null, IServerProbe? probe = null) =>
        new(launcher, prover ?? new FakeIdentityProver(), probe ?? new FakeServerProbe(ProbeVerdict.NotListening),
            new PlainHttpClientFactory(), NullLoggerFactory.Instance, processPath, config);

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
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54242/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        prover.AnswerNext(null); // the fallback child proves under this root's key
        var logger = new FakeLogger();
        var config = Config(54242, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Answered), prover, launcher, config, logger);

        outcome.Result.Url.ShouldBe("http://127.0.0.1:54242/mcp");
        outcome.Fallback.ShouldBeTrue();
        launcher.AttachCalls.ShouldBe(0, "an unproven listener is never attached to");
        launcher.PrivateCalls.ShouldBe(1);
        launcher.PrivateArguments.ShouldContain("--port");
        launcher.PrivateArguments[Array.IndexOf(launcher.PrivateArguments, "--port") + 1].ShouldBe("0");
        var record = logger.Collector.GetSnapshot().Single(r => r.Id == 690);
        record.Message.ShouldContain("54242");
        record.Message.ShouldContain("stop the listener");
        record.Message.ShouldContain("BadSignature");
    }

    [Fact]
    public async Task AcquireShared_WithAnUnansweredProbe_ChallengesThenFallsBack()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54243/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.Timeout);
        prover.AnswerNext(null);
        var config = Config(54243, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Unanswered), prover, launcher, config);

        prover.Calls[0].ShouldBe(new Uri("http://127.0.0.1:54243/mcp"),
            "an answered probe is not the only path that challenges before deciding");
        outcome.Fallback.ShouldBeTrue();
        launcher.PrivateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AcquireShared_WhenTheFallbackChildDoesNotProve_HandsBackNoUrl()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54244/mcp", null));
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        var config = Config(54244, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.Answered), prover, launcher, config);

        outcome.Result.Url.ShouldBeNull("a fallback child that does not prove must never be handed to the token-bearing caller");
        outcome.Fallback.ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireShared_WhenTheStartedListenerCannotProve_FallsBackPrivately()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54245/mcp", null));
        // The configured-port start hands back a URL, but a racer owns it: proof fails there, then
        // the fallback child proves.
        var prover = new FakeIdentityProver(IdentityProofFailure.BadSignature);
        prover.AnswerNext(null);
        var config = Config(54245, "/tmp/unused");

        var outcome = await AcquireAsync(new FakeServerProbe(ProbeVerdict.NotListening), prover, launcher, config);

        outcome.Fallback.ShouldBeTrue();
        launcher.AttachCalls.ShouldBe(1);
        launcher.PrivateCalls.ShouldBe(1);
        outcome.Result.Url.ShouldBe("http://127.0.0.1:54245/mcp");
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

    private sealed class FakeBackendLauncher : IBackendLauncher
    {
        private readonly Exception? _throws;
        private readonly BackendResult _result;

        public FakeBackendLauncher(BackendResult result) => _result = result;
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
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
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
