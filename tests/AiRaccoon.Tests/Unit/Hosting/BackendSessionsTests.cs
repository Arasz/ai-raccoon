using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     The proxy's half of auto-start: <see cref="BackendSessions" /> acquires the backend through
///     <see cref="IBackendLauncher" /> with an explicit process path, so the refusal and launch
///     paths are pinned without a real spawn and without depending on how the test host itself was
///     launched. The forwarder above it is covered by <c>ProxyForwardTests</c>.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackendSessionsTests
{
    private const string AppHost = "/opt/ai-raccoon/ai-raccoon";
    private const string DotnetHost = "/usr/local/share/dotnet/dotnet";

    private static ServerConfig Config(int port, string dataRoot) =>
        new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User });

    private static BackendSessions Subject(IBackendLauncher launcher, string? processPath, ServerConfig config) =>
        new(launcher, new PlainHttpClientFactory(), NullLoggerFactory.Instance, processPath, config);

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
    public async Task OpenAsync_WhenTheLauncherFindsNoUrl_ThrowsUnavailable_NamingThePort_AndStderr()
    {
        var launcher = new FakeBackendLauncher(new BackendResult(null, 3, "ai-raccoon: could not decrypt the bank"));
        await using var sessions = Subject(launcher, AppHost, Config(54232, "/tmp/unused"));

        var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
            sessions.OpenAsync(null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("54232");
        error.Message.ShouldContain("could not decrypt the bank");
        launcher.FileName.ShouldBe(AppHost);
    }

    [Fact]
    public async Task OpenAsync_WhenTheLauncherThrowsBackendStart_WrapsAsUnavailable()
    {
        var launcher = new FakeBackendLauncher(new BackendStartException("could not start it", new InvalidOperationException()));
        await using var sessions = Subject(launcher, AppHost, Config(54233, "/tmp/unused"));

        var error = await Should.ThrowAsync<BackendUnavailableException>(() =>
            sessions.OpenAsync(null, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("could not start it");
    }

    [Fact]
    public async Task OpenAsync_WhenTheDataRootHoldsNoToken_ThrowsUnavailable_NamingTheTokenPath()
    {
        var dataRoot = TestData.CreateTempRoot("backend-sessions-no-token");
        try
        {
            var launcher = new FakeBackendLauncher(new BackendResult("http://127.0.0.1:1/mcp", null));
            await using var sessions = Subject(launcher, AppHost, Config(1, dataRoot));

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

        public string? FileName { get; private set; }

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Calls++;
            FileName = fileName;
            return _throws is null ? Task.FromResult(_result) : Task.FromException<BackendResult>(_throws);
        }
    }
}
