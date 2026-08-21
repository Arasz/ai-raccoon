using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP6 — SqliteCodeSearchService, the FTS5-only v1 leg of code search
///     (docs/work/2026-08-21-code-search-implementation-plan.md §3.6): project scoping, ranking
///     normalization (rank 1 == 1.0, mirroring memory's RRF), minRelativeScore, empty-corpus, and
///     code_get's hash round-trip. Rows are seeded directly by SQL (no CodeIngestor yet — WP3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteCodeSearchServiceTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-search-tests");
    private SqliteConnectionFactory _factory = null!;
    private SqliteCodeSearchService _service = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _service = new SqliteCodeSearchService(_factory);
        // Opening the bank once runs MemorySchema.EnsureAsync, so the code corpus tables exist
        // before any test seeds rows into them.
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SearchAsync_MatchesSeededRows_AndCarriesPathAndLineRange()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Foo.cs", value: "sealed class QuokkaFinder { }",
            lineStart: 1, lineEnd: 3);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "QuokkaFinder", 20, 0.0),
            TestContext.Current.CancellationToken);

        var hit = results.Results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe("hash-1");
        hit.Path.ShouldBe("src/Foo.cs");
        hit.LineStart.ShouldBe(1);
        hit.LineEnd.ShouldBe(3);
        hit.Ranking.ShouldBe(1.0, "the top (only) hit is always max-normalized to 1.0, mirroring memory's RRF");
    }

    [Fact]
    public async Task SearchAsync_NeverLeaksAcrossProjects()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Program.cs", value: "class WombatRunner { }",
            lineStart: 1, lineEnd: 1);
        await SeedAsync(id: 2, projectId: "other", path: "src/Program.cs", value: "class WombatRunner { }",
            lineStart: 1, lineEnd: 1);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "WombatRunner", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.ShouldHaveSingleItem().Hash.ShouldBe("hash-1");
    }

    [Fact]
    public async Task SearchAsync_MinRelativeScore_DropsTheWeakerHit()
    {
        // Two matches for the same term, seeded so rank 2's normalized score (61/62) sits below a
        // floor that rank 1 (1.0) always clears.
        await SeedAsync(id: 1, projectId: "acme", path: "src/A.cs", value: "class DingoTracker DingoTracker DingoTracker { }",
            lineStart: 1, lineEnd: 1);
        await SeedAsync(id: 2, projectId: "acme", path: "src/B.cs", value: "class DingoTracker { }",
            lineStart: 1, lineEnd: 1);

        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "DingoTracker", 20, 0.99),
            TestContext.Current.CancellationToken);

        results.Results.ShouldHaveSingleItem("minRelativeScore=0.99 keeps only rank 1 (1.0); rank 2 normalizes below it");
    }

    [Fact]
    public async Task SearchAsync_EmptyCorpus_ReturnsEmptyResults_WithoutError()
    {
        var results = await _service.SearchAsync(new CodeSearchQuery("acme", "anything", 20, 0.0),
            TestContext.Current.CancellationToken);

        results.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_KnownHash_ReturnsFullSourceWithPathAndRange()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Bar.cs", value: "sealed class NarwhalTusk { }",
            lineStart: 5, lineEnd: 9);

        var entry = await _service.GetAsync("acme", "hash-1", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry!.Value.ShouldBe("sealed class NarwhalTusk { }");
        entry.Path.ShouldBe("src/Bar.cs");
        entry.LineStart.ShouldBe(5);
        entry.LineEnd.ShouldBe(9);
    }

    [Fact]
    public async Task GetAsync_UnknownHash_ReturnsNull()
    {
        var entry = await _service.GetAsync("acme", "does-not-exist", TestContext.Current.CancellationToken);

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_KnownHashInAnotherProject_ReturnsNull()
    {
        await SeedAsync(id: 1, projectId: "other", path: "src/Bar.cs", value: "class X { }", lineStart: 1, lineEnd: 1);

        var entry = await _service.GetAsync("acme", "hash-1", TestContext.Current.CancellationToken);

        entry.ShouldBeNull();
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
}
