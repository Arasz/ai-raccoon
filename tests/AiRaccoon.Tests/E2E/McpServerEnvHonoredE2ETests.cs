using AiRaccoon.Infrastructure.Embedding;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Proves the env path end-to-end after the CLI refactor: AIRACCOON_INSTALL_SCOPE is the
///     honored middle precedence layer, so the server builds the bank for the project scope.
///     The var is set before the first CreateClientAsync (host build is lazy) and restored in
///     finally so it cannot leak into the serial E2E collection.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public class McpServerEnvHonoredE2ETests : IAsyncLifetime
{
    private const string ScopeEnvVar = "AIRACCOON_INSTALL_SCOPE";

    private McpClient _client = null!;
    private McpServerFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await BundledModel.EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory();

        var previous = Environment.GetEnvironmentVariable(ScopeEnvVar);
        Environment.SetEnvironmentVariable(ScopeEnvVar, "project");
        try
        {
            _client = await _factory.CreateClientAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScopeEnvVar, previous);
        }
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
    public async Task InstallScope_ProjectEnv_BankLivesUnderDataRootAiRaccoonDir()
    {
        // Any tool call opens the bank; stats is the lightest.
        await _client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
            null, null, CancellationToken.None);

        // Project scope nests the bank at <dataRoot>/.ai-raccoon/memory.db.
        File.Exists(Path.Combine(_factory.DataRoot, ".ai-raccoon", "memory.db")).ShouldBeTrue();
        File.Exists(Path.Combine(_factory.DataRoot, "memory.db")).ShouldBeFalse();
    }
}
