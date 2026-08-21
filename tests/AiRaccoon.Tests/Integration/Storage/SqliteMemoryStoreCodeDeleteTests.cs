using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP3-partial digest delete-BOTH legs (docs/work/2026-08-21-code-search-implementation-plan.md
///     §3.5): `DeleteSourcePathAsync` and the replace-by-path transaction remove code rows in the
///     same transaction as the memory legs — no separate digest-level classification needed, the
///     store deletes from both corpora unconditionally and each ingestor self-filters on re-ingest.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreCodeDeleteTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _conn;
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-delete");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public SqliteMemoryStoreCodeDeleteTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _conn = _factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        var sourceStore = new SqliteMemorySourceStore(_factory);
        var timeProvider = new FakeTimeProvider(FixedNow);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var embedder = new EntryEmbedder(TestData.CreateEmbeddingService(), Substitute.For<IModelMigrationLease>(), timeProvider);
        var codeIngestor = new CodeIngestor(new CodeFileTypeMatcher(), new StubCodeChunker(), timeProvider);
        var fileIngestor = new FileIngestor(matcher, embedder, sourceStore, timeProvider, new LocalTokenizer(),
            codeFileTypeMatcher: new CodeFileTypeMatcher(), codeIngestor: codeIngestor);
        _store = new SqliteMemoryStore(_factory, sourceStore, fileIngestor, embedder, timeProvider,
            NullLogger<SqliteMemoryStore>.Instance, new NoiseFilteringService([]), new SqliteSettingsStore(_factory));
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    [Fact]
    public async Task DeleteSourcePathAsync_RemovesCodeRows_ForThatPath()
    {
        var file = await WriteAsync("Program.cs", "class Program\n{\n}\n");
        await SeedCodeRowAsync(file, "acme");

        (await CountCodeAsync(file)).ShouldBeGreaterThan(0);

        await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        (await CountCodeAsync(file)).ShouldBe(0);
    }

    [Fact]
    public async Task DeleteSourcePathAsync_DoesNotTouchOtherProjectsCodeRows()
    {
        var file = await WriteAsync("Program.cs", "class Program\n{\n}\n");
        await SeedCodeRowAsync(file, "acme");
        await SeedCodeRowAsync(file, "other-project");

        await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        (await CountCodeAsync(file, "acme")).ShouldBe(0);
        (await CountCodeAsync(file, "other-project")).ShouldBeGreaterThan(0);
    }

    /// <summary>WP4-T29's shape: the per-digest-event delete leg must use an index on (project_id,
    /// path), not a full table scan — code chunk counts per project are an order of magnitude
    /// larger than notes. idx_code_entries_path was dropped as a redundant left-prefix of
    /// uq_code_chunk(project_id, path, hash) (integration review): the planner already picks
    /// uq_code_chunk for this query, so that is what the test pins.</summary>
    [Fact]
    public async Task DeleteBySourcePath_UsesTheCodeChunkIndex_NotAFullTableScan()
    {
        var rows = await _conn.QueryAsync<dynamic>(
            "EXPLAIN QUERY PLAN " + MemorySql.DeleteCodeBySourcePath,
            new { projectId = "acme", path = "/does/not/matter.cs", pathPrefix = "/does/not/matter.cs/%" });
        var plan = string.Join(" | ", rows.Select(r => (string)r.detail));

        plan.ShouldContain("uq_code_chunk", Case.Sensitive,
            $"the code delete leg must use uq_code_chunk, not a full-table scan; actual plan: {plan}");
    }

    [Fact]
    public async Task ReplaceIfFileChangedAsync_DeletesOldCodeChunks_AndReingestsNewOnes()
    {
        var file = await WriteAsync("A.cs", "class Old\n{\n}\n");
        await AllowScopeAsync();
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        var oldHashes = (await CodeHashesAsync(file)).ToArray();
        oldHashes.ShouldNotBeEmpty();

        await File.WriteAllTextAsync(file, "class Brand\n{\n}\n\nclass New\n{\n}\n",
            TestContext.Current.CancellationToken);
        var newHash = WatchDigestHash(file);

        var replaced = await _store.ReplaceIfFileChangedAsync("acme", file, newHash,
            TestContext.Current.CancellationToken);

        replaced.ShouldBeTrue();
        var currentHashes = (await CodeHashesAsync(file)).ToArray();
        currentHashes.ShouldNotBeEmpty();
        currentHashes.ShouldNotContain(h => oldHashes.Contains(h));
    }

    [Fact]
    public async Task ReplaceIfFileChangedAsync_MdChanged_CodeLegIsNoOp()
    {
        var file = await WriteAsync("README.md", "# hello\n");
        await AllowScopeAsync();
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(file, "# hello again\n", TestContext.Current.CancellationToken);
        var newHash = WatchDigestHash(file);

        var replaced = await _store.ReplaceIfFileChangedAsync("acme", file, newHash,
            TestContext.Current.CancellationToken);

        replaced.ShouldBeTrue();
        (await CountCodeAsync(file)).ShouldBe(0);
    }

    private static string WatchDigestHash(string path) =>
        WatchDigestExecutor.ComputeHash(IngestPath.Normalize(path), File.ReadAllText(path));

    private async Task<string> WriteAsync(string name, string content)
    {
        var path = Path.Combine(_dataRoot, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private Task AllowScopeAsync() =>
        _conn.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value)",
            new { key = IngestScopeKeys.ScopeGlobal, value = IngestScopeKeys.Serialize([_dataRoot]) });

    /// <summary>Direct-SQL fixture seed — proves the delete leg alone, independent of CodeIngestor.</summary>
    private async Task SeedCodeRowAsync(string path, string projectId)
    {
        var normalized = IngestPath.Normalize(path);
        await _conn.ExecuteAsync(
            """
            INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at, chunk_index, total_chunks)
            VALUES (@hash, @path, 'seed', @path, 1, 1, @projectId, 0, 0, 0, 1)
            """,
            new { hash = Guid.NewGuid().ToString("N"), path = normalized, projectId });
    }

    private async Task<int> CountCodeAsync(string path, string projectId = "acme") =>
        await _conn.ExecuteScalarAsync<int>("SELECT count(*) FROM code_entries WHERE path = @path AND project_id = @projectId",
            new { path = IngestPath.Normalize(path), projectId });

    private async Task<IEnumerable<string>> CodeHashesAsync(string path) =>
        await _conn.QueryAsync<string>("SELECT hash FROM code_entries WHERE path = @path",
            new { path = IngestPath.Normalize(path) });
}
