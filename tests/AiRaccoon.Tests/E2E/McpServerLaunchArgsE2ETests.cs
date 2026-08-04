using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Proves the launch-identity flags end-to-end after the single-channel refactor:
///     --install-scope=project (injected via the factory's UseSetting, which the real
///     entry point receives as an arg) makes the server build the bank for the project
///     scope. Env vars are no longer a config channel.
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
        await BundledModel.EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory(InstallScope.Project);
        _client = await _factory.CreateClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _factory?.Dispose();
    }

    [Fact]
    public async Task InstallScope_ProjectFlag_BankLivesUnderDataRootAiRaccoonDir()
    {
        // Any tool call opens the bank; stats is the lightest.
        await _client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
            null, null, CancellationToken.None);

        // Project scope nests the bank at <dataRoot>/.ai-raccoon/memory.db.
        File.Exists(Path.Combine(_factory.DataRoot, ".ai-raccoon", "memory.db")).ShouldBeTrue();
        File.Exists(Path.Combine(_factory.DataRoot, "memory.db")).ShouldBeFalse();
    }
}
