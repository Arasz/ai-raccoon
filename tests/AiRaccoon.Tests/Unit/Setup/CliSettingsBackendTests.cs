using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP7 §5.1: the CLI's half of "auto-start reuses BackendLauncher as-is". Exercised against a
///     fake <see cref="IBackendLauncher" /> and an explicit process path, so the acquire/token/wrap
///     logic is pinned without a real process spawn and without depending on how the test host
///     itself was launched — the real spawn is covered end to end by
///     <see cref="AiRaccoon.Tests.Integration.Setup.ServerSettingsStoreTests" /> and the CLI-contract
///     suites.
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
            await new McpTokenFile(dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));

            var store = await CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(1, dataRoot),
                TestContext.Current.CancellationToken);

            store.ShouldBeOfType<ServerSettingsStore>();
            launcher.FileName.ShouldBe(AppHost);
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
            CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(0, "/tmp/unused"), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("--port 0");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheProcessIsTheDotnetHost_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54220/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, DotnetHost, Config(54220, "/tmp/unused"), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("dotnet host");
        error.Message.ShouldContain("serve --port 54220");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheProcessPathIsUnknown_ThrowsUnavailable_WithoutCallingTheLauncher()
    {
        var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:54221/mcp", null));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, null, Config(54221, "/tmp/unused"), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("unknown");
        launcher.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherFindsNoUrl_ThrowsUnavailable_NamingThePort()
    {
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(54217, "/tmp/unused"), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("54217");
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherFindsNoUrl_ButCapturedStderr_IncludesItInTheMessage()
    {
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "ai-raccoon: could not decrypt the bank"));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(54219, "/tmp/unused"), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("could not decrypt the bank");
    }

    [Fact]
    public async Task AcquireAsync_WhenTheLauncherThrowsBackendStart_WrapsAsUnavailable()
    {
        var launcher = new FakeBackendLauncher(new BackendStartException("could not start it", new InvalidOperationException()));

        var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
            CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(54218, "/tmp/unused"), TestContext.Current.CancellationToken));

        error.Message.ShouldContain("could not start it");
    }

    [Fact]
    public async Task AcquireAsync_WhenTheDataRootHoldsNoToken_ThrowsUnavailable_NamingTheTokenPath()
    {
        var dataRoot = TestData.CreateTempRoot("cli-settings-backend-no-token");
        try
        {
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));

            var error = await Should.ThrowAsync<SettingsServerUnavailableException>(() =>
                CliSettingsBackend.AcquireAsync(launcher, AppHost, Config(1, dataRoot), TestContext.Current.CancellationToken));

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

        public int Calls { get; private set; }

        public string? FileName { get; private set; }

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Calls++;
            FileName = fileName;
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
        }
    }
}
