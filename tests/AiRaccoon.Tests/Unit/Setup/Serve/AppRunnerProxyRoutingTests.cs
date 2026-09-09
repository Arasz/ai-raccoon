using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Settings;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     Q4/P2 gap: every non-command launch must route to the proxy — the stdio plain host and the
///     bare-http full server are deleted (D2b), so there is no in-process fallback left to reach.
///     An unpackaged host (processPath "dotnet") cannot auto-start a backend, so the proxy fails
///     fast with ProxyBackendUnavailable(6): an exit only the proxy path produces, with no spawn,
///     no port bind and no bank open — fast enough for Fast.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AppRunnerProxyRoutingTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-proxy-routing");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Theory]
    [InlineData()] // bare launch, the default proxy entry point (ADR-0020)
    [InlineData("--transport", "proxy")] // the explicit default
    [InlineData("--transport", "http")] // D2b: bare-http is removed too — still the proxy, never a server
    public async Task BareLaunch_AlwaysRoutesToTheProxy(params string[] transportFlag)
    {
        var runner = new AppRunner((_, _, _) => Task.FromResult<ISettingsStore>(new InMemorySettings()), "dotnet");

        var exit = await runner.Run(["--data-root", _dataRoot, "--port", "7721", .. transportFlag]);

        exit.ShouldBe(ExitCode.ProxyBackendUnavailable);
        // Secondary observable: the proxy path wires shutdown-signal cancellation; a launch that
        // returned early (parse failure, help, a verb) would leave this at zero.
        runner.ShutdownCancellationRegistrations.ShouldBe(1);
        // The failed proxy started nothing: no bank, no token file under the data root.
        File.Exists(Path.Combine(_dataRoot, "memory.db")).ShouldBeFalse();
        File.Exists(Path.Combine(_dataRoot, McpTokenFile.FileName)).ShouldBeFalse();
    }
}
