using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     WP6 end-to-end: memory_search kind=code/both against the REAL SqliteCodeSearchService over
///     a real bank, seeded by SQL (no CodeIngestor yet -- WP3). Proves the FTS5-only-mode warning
///     and the real hits arrive together, and that scope=shared empties the code section through
///     the real service too (not just a unit-level spy).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySearchCodeIntegrationTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-search-e2e-tests");
    private SqliteConnectionFactory _factory = null!;
    private MemoryTools _tools = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var settings = new SqliteSettingsStore(_factory);
        var store = new SettingsOnlyStore(settings);
        var access = new MemoryAccessGuard(store);
        var gate = new ToolGate(access, new FakePromotionQueue());
        _tools = new MemoryTools(store, gate, new NoOpSearchQualityService(), new QueryGuardService(settings),
            new MemoryWriteService(store, new FakePromotionQueue()), new NoOpMeasurementRecorder(),
            new SqliteCodeSearchService(_factory), NullLogger<MemoryTools>.Instance);
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Search_KindCode_ReturnsRealFtsHits_WithTheEngineNotConfiguredWarning()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Wombat.cs", value: "sealed class WombatRunner { }",
            lineStart: 1, lineEnd: 3);

        var envelope = await _tools.Search("acme", "WombatRunner", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        var hit = envelope.Data!.Code.ShouldNotBeNull().ShouldHaveSingleItem();
        hit.Path.ShouldBe("src/Wombat.cs");
        hit.LineStart.ShouldBe(1);
        hit.LineEnd.ShouldBe(3);
        envelope.Data!.Warning.ShouldNotBeNull().ShouldContain(CodeSearchWarnings.EngineNotConfigured);
    }

    [Fact]
    public async Task Search_KindCode_SharedScope_ReturnsEmptyCodeSection_EvenWithRowsInTheBank()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Wombat.cs", value: "sealed class WombatRunner { }",
            lineStart: 1, lineEnd: 3);

        var envelope = await _tools.Search("acme", "WombatRunner", scope: "shared", kind: "code",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Code.ShouldNotBeNull();
        envelope.Data!.Code.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_KindMemory_Default_ResponseHasNoCodeKeyAtAll_EvenWithCodeRowsInTheBank()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Wombat.cs", value: "sealed class WombatRunner { }",
            lineStart: 1, lineEnd: 3);

        var envelope = await _tools.Search("acme", "WombatRunner",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Code.ShouldBeNull();
    }

    private async Task SeedAsync(long id, string projectId, string path, string value, int lineStart, int lineEnd)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @path, @lineStart, @lineEnd, @projectId, 1, 1)
            """,
            new { id, hash = $"hash-{id}", path, value, lineStart, lineEnd, projectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>MemoryTools' Search path here needs settings plus an empty memory leg -- this suite's memory rows always live in code_entries, never entries.</summary>
    private sealed class SettingsOnlyStore(SqliteSettingsStore settings) : FakeMemoryStore
    {
        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults([], SearchTimings.Empty));

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            settings.GetSettingAsync(key, cancellationToken);

        public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
            settings.SetSettingAsync(key, value, cancellationToken);

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            settings.GetSettingsByPrefixAsync(prefix, cancellationToken);
    }
}
