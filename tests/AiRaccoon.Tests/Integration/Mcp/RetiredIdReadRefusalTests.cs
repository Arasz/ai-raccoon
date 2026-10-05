using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.E2E;
using AiRaccoon.Tests.Unit.Projects;
using Dapper;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     D2 over the wire: a read under an id the project-ids repair dropped is refused with
///     <c>project-retired</c>, also while a repair request holds the finish marker open. The
///     server warms its alias cache from the durable table at startup, the way production does.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class RetiredIdReadRefusalTests : IAsyncLifetime
{
    private static readonly string Dropped = Guid.CreateVersion7().ToString("D");

    private readonly McpServerFactory _factory = new() { Projects = [Dropped] };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        ProjectIdAliasMap.ResetDefault();
    }

    [RetryFact]
    public async Task ReadUnderDroppedId_OverTheWire_IsProjectRetired()
    {
        await SeedDroppedAsync(openRepairRequest: false);
        await using var client = await _factory.CreateClientAsync();

        (await RefusalAsync(client, "memory_search",
                new() { ["projectId"] = Dropped, ["query"] = "x", ["sessionId"] = "sess-retired" }))
            .ShouldStartWith("project-retired:");
        (await RefusalAsync(client, "memory_stats", new() { ["projectId"] = Dropped }))
            .ShouldStartWith("project-retired:");
    }

    [RetryFact]
    public async Task ReadUnderDroppedId_WithAnOpenRepairRequest_IsProjectRetired()
    {
        await SeedDroppedAsync(openRepairRequest: true);
        await using var client = await _factory.CreateClientAsync();

        (await RefusalAsync(client, "memory_search",
                new() { ["projectId"] = Dropped, ["query"] = "x", ["sessionId"] = "sess-retired" }))
            .ShouldStartWith("project-retired:");
    }

    private static async Task<string> RefusalAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        result.IsError.ShouldBe(true, $"{tool} accepted a retired project id");
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    /// <summary>Persists a drop the way a repair apply does, before the server starts and warms its cache.</summary>
    private async Task SeedDroppedAsync(bool openRepairRequest)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = TestData.CreateInfrastructureOptions(_factory.DataRoot);
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        await using var connection = await factory.OpenBankAsync(ct);
        await ProjectIdAliases.PersistAppliedAsync(connection, new ProjectIdAliasMap([], [], [Dropped]),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
        if (openRepairRequest)
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), mapJson = (string?)null },
                cancellationToken: ct));
        }
    }
}
