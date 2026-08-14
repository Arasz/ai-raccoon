using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Proves the launch-identity flags end-to-end: --install-scope=project (injected via the
///     factory's UseSetting, mirroring the real entry point's arg) makes the server build the
///     bank for the project scope, not via env vars.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public class McpServerLaunchArgsE2ETests : IAsyncLifetime
{
    private McpClient _client = null!;
    private McpServerFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory(InstallScope.Project);
        _client = await _factory.CreateClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task InstallScope_ProjectFlag_BankLivesUnderDataRootAiRaccoonDir()
    {
        // Any tool call opens the bank; stats is the lightest.
        await _client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
            null, null, CancellationToken.None);

        File.Exists(Path.Combine(_factory.DataRoot, ".ai-raccoon", "memory.db")).ShouldBeTrue();
        File.Exists(Path.Combine(_factory.DataRoot, "memory.db")).ShouldBeFalse();
    }

    /// <summary>
    ///     ADR-0020's escape hatch: --transport stdio stays a complete in-process server. The port it
    ///     is given is free and stays free — nothing is proxied and no backend is started.
    /// </summary>
    [Fact]
    public async Task ExplicitStdio_StillServesTheFullToolSurfaceInProcess()
    {
        var dataRoot = TestData.CreateTempRoot("explicit-stdio");
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        try
        {
            lease.ReleaseForBind();
            await using var client = await AiRaccoonProcess.ConnectAsync(
                ["--transport", "stdio", "--data-root", dataRoot, "--port", port.ToString()],
                TestContext.Current.CancellationToken);

            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            var result = await client.CallToolAsync("memory_stats",
                new Dictionary<string, object?> { ["projectId"] = "acme" },
                cancellationToken: TestContext.Current.CancellationToken);

            tools.Count.ShouldBe(25);
            result.IsError.ShouldNotBe(true);
            // The in-process server opened its own bank, and nothing was ever spawned on the port.
            File.Exists(Path.Combine(dataRoot, "memory.db")).ShouldBeTrue();
            (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
                .ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dataRoot, true);
        }
    }
}
