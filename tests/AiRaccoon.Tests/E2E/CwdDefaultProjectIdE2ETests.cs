using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Cwd-default projectId resolution over the real HTTP MCP server: a tool call that omits
///     projectId resolves to the project whose ingest scope contains the server's working
///     directory (H1: the in-process factory shares the test process cwd — this test passing is
///     the proof), while an empty project-scope table keeps the enriched refusal.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class CwdDefaultProjectIdE2ETests : IAsyncLifetime
{
    private const string ProjectId = "laneA";

    private McpClient _client = null!;
    private McpServerFactory _factory = null!;
    private FakeEmbeddingEndpoint _openAi = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory();
        _client = await _factory.CreateClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _factory.DisposeAsync();
        await _openAi.DisposeAsync();
    }

    [RetryFact]
    public async Task MemorySearch_NoProjectId_ResolvesFromCwdScope()
    {
        // The seeding pattern of McpServerToolSurfaceE2ETests: open the instance's bank directly
        // and write the scope row for a NON-guid project id, proving stored ids flow verbatim.
        await SeedScopeAsync(ProjectId, [Directory.GetCurrentDirectory()]);

        var write = await CallAsync("memory_write", ("projectId", ProjectId),
            ("content", "cwd default resolution fact xyzzy"));
        Text(write).ShouldContain("\"hash\"");

        // No projectId argument at all — the server resolves laneA from the cwd's scope row.
        var search = await CallAsync("memory_search", ("query", "cwd default resolution fact xyzzy"), ("sessionId", "sess-e2e"));
        Text(search).ShouldContain("cwd default resolution fact xyzzy");

        // The resolved id is laneA on a second tool too — stats is scoped to the resolved project.
        var stats = await CallAsync("memory_stats");
        Text(stats).ShouldContain("project:laneA");
    }

    [RetryFact]
    public async Task NoProjectId_WithEmptyScopeTable_RefusesWithCwdEnrichedMessage()
    {
        // The factory seeds ingest.scope.global (TempPath + DataRoot), but the resolver skips the
        // global key, and no watch rows exist: no project surface contains the cwd.
        var result = await _client.CallToolAsync("memory_stats", new Dictionary<string, object?>(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Cwd-tolerant: assert the stable prefix + tail around the probed working directory.
        var text = RefusalText(result);
        text.ShouldStartWith(
            "invalid-params: projectId is required (no registered project's scope contains cwd ");
        text.ShouldContain(
            "; pass projectId explicitly, or register this directory with memory_watch_add / settings ingest scope add)");
    }

    /// <summary>
    ///     Air-merge P3's wire case: on a migrated bank (P2 finished marker seeded directly) a write
    ///     under a true typo is refused with project-not-registered — and the refusal registers
    ///     nothing (zero new projects rows). The bank asserts beside the wire text are the
    ///     non-snapshot wire-code assertions: no snapshot is regenerated to force this green.
    ///     Ledger — always-auto-register : --filter MemoryWrite_WithTrueTypo_AfterRepair_RefusesProjectNotRegistered :
    ///     migrated bank (P2 finished marker seeded), laneA-typo write via the real MCP server + projects-row count.
    /// </summary>
    [RetryFact]
    public async Task MemoryWrite_WithTrueTypo_AfterRepair_RefusesProjectNotRegistered()
    {
        await SeedFinishedMarkerAsync();

        var result = await CallAsync("memory_write", ("projectId", "laneA-typo"),
            ("content", "this typo must not become a project"));

        RefusalText(result).ShouldStartWith("project-not-registered:");
        (await CountProjectsAsync("laneA-typo")).ShouldBe(0,
            "a refused typo must not create a projects row");
    }

    private async Task SeedScopeAsync(string projectId, string[] paths)
    {
        var options = new InfrastructureOptions { DataRoot = _factory.DataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), TimeProvider.System,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject(projectId), IngestScopeKeys.Serialize(paths));
    }

    /// <summary>Stamps the P2 finished marker directly — the migration gate P3 enforcement reads.</summary>
    private async Task SeedFinishedMarkerAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _factory.DataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = 1L }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishRepairRequest,
            new { kind = RepairKinds.ProjectIds, finishedAt = 2L }, cancellationToken: ct));
    }

    private async Task<long> CountProjectsAsync(string projectId)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _factory.DataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM projects WHERE id = @id", new { id = projectId },
            cancellationToken: ct));
    }

    private async Task<CallToolResult> CallAsync(string tool, params (string Key, object? Value)[] arguments)
    {
        var dict = arguments.ToDictionary(a => a.Key, a => a.Value);
        return await _client.CallToolAsync(tool, dict, null, null, TestContext.Current.CancellationToken);
    }

    private static string Text(CallToolResult result)
    {
        // isError is absent (null) on success in the MCP protocol — only true on failures.
        result.IsError.ShouldNotBe(true);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    private static string RefusalText(CallToolResult result)
    {
        result.IsError.ShouldBe(true);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }
}
