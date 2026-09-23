using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP7 §5.1: the CLI's half of "auto-start reuses BackendLauncher as-is". Owner ruling
///     2026-09-22 reverted this path off F70/K1's private spawn (which it briefly carried) back to
///     the legacy attach-or-start shared acquire — "no own backend - attach - the same rules as
///     usual" — so every settings-routed verb reuses whatever already answers on
///     <see cref="ServerConfig.Port" /> and starts one there when nothing does, with no
///     <c>--attach</c> needed (see ADR-0105). Exercised against a fake <see cref="IBackendLauncher" />
///     and an explicit process path, so the acquire/token/wrap logic is pinned without a real
///     process spawn and without depending on how the test host itself was launched — the real spawn
///     is covered end to end by <see cref="AiRaccoon.Tests.Integration.Setup.ServerSettingsStoreTests" />
///     and the CLI-contract suites.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliSettingsBackendTests
{
    /// <summary>A packaged apphost: what Environment.ProcessPath names for an installed ai-raccoon.</summary>
    private const string AppHost = "/opt/ai-raccoon/ai-raccoon";

    /// <summary>The dotnet muxer: what Environment.ProcessPath names under `dotnet exec`/`dotnet run`.</summary>
    private const string DotnetHost = "/usr/local/share/dotnet/dotnet";

    private static ServerConfig Config(int port, string dataRoot) =>
        new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User });

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherFindsAUrlAndTheTokenFileMatches_ReturnsAWorkingStore()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-backend-ok");
        try
        {
            // F39: the shared acquire refuses an empty non-default root before it probes, so a
            // fixture that means to exercise the launcher/token path must hold a real bank.
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));

            var store = await CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(1, dataRoot),
                new FakeLogger(), TestContext.Current.CancellationToken);

            store.ShouldBeOfType<ServerSettingsStore>();
            launcher.FileName.ShouldBe(AppHost);
            launcher.AcquireCalls.ShouldBe(1);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     F38 residual (owner ruling N1, 2026-09-22): the 4h idle default stays — ruled out of
    ///     scope — but a successful acquire must say the backend survives this command and how to
    ///     stop it. Before this, the only line was "starting the backend on port N".
    /// </summary>
    [Fact]
    public async Task AcquireAsync_WhenItSucceeds_LogsThatTheBackendOutlivesTheCommand()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-backend-disclosure");
        try
        {
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54222/mcp", null));
            var logger = new FakeLogger();

            await CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(54222, dataRoot), logger,
                TestContext.Current.CancellationToken);

            var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
            record.Message.ShouldContain("54222");
            record.Message.ShouldContain("keeps running after this command exits");
            record.Message.ShouldContain("serve --restart --attach --port 54222");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenThePortIsOutOfRange_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:0/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(0, "/tmp/unused"), new FakeLogger(),
                TestContext.Current.CancellationToken));

        error.Message.ShouldContain("--port 0");
        launcher.AcquireCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheProcessIsTheDotnetHost_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54220/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, DotnetHost, Config(54220, "/tmp/unused"), new FakeLogger(),
                TestContext.Current.CancellationToken));

        error.Message.ShouldContain("dotnet host");
        error.Message.ShouldContain("serve --port 54220");
        launcher.AcquireCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheProcessPathIsUnknown_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54221/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, null, Config(54221, "/tmp/unused"), new FakeLogger(),
                TestContext.Current.CancellationToken));

        error.Message.ShouldContain("unknown");
        launcher.AcquireCalls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherFindsNoUrl_ThrowsUnavailable_NamingThePort()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("cli-settings-backend-no-url", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendResult(null, 3));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(54217, dataRoot), new FakeLogger(),
                    TestContext.Current.CancellationToken));

            error.Message.ShouldContain("54217");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherFindsNoUrl_ButCapturedStderr_IncludesItInTheMessage()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("cli-settings-backend-stderr", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "ai-raccoon: could not decrypt the bank"));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(54219, dataRoot), new FakeLogger(),
                    TestContext.Current.CancellationToken));

            error.Message.ShouldContain("could not decrypt the bank");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherThrowsBackendStart_WrapsAsUnavailable()
    {
        var dataRoot = await TestData.CreateTempRootWithBankAsync("cli-settings-backend-start-throws", TestContext.Current.CancellationToken);
        try
        {
            var launcher = new FakeBackendLauncher(new BackendStartException("could not start it", new InvalidOperationException()));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(54218, dataRoot), new FakeLogger(),
                    TestContext.Current.CancellationToken));

            error.Message.ShouldContain("could not start it");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Fact]
    public async Task AcquireAsync_WhenTheDataRootHoldsNoToken_ThrowsUnavailable_NamingTheTokenPath()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-backend-no-token");
        try
        {
            // The bank must exist (F39) while the token deliberately does not — that absence is
            // the verdict this test pins.
            await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(dataRoot), TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(1, dataRoot), new FakeLogger(),
                    TestContext.Current.CancellationToken));

            error.Message.ShouldContain(McpTokenFile.FileName);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private sealed class FakeBackendLauncher : IBackendLauncher
    {
        private readonly Exception? _throws;
        private readonly BackendResult _result;

        public FakeBackendLauncher(BackendResult result) => _result = result;
        public FakeBackendLauncher(Exception throws) => _throws = throws;

        public int AcquireCalls { get; private set; }

        public string? FileName { get; private set; }

        public Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            throw new InvalidOperationException("the shared attach-or-start path must never private-spawn (owner ruling 2026-09-22)");

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            AcquireCalls++;
            FileName = fileName;
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
        }
    }
}
