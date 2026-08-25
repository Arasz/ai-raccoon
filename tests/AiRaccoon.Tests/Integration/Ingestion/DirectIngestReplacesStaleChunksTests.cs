using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     Defect B (docs/work/2026-08-21-restart-probe-and-reingest-stale-chunks-plan.md): re-ingesting
///     a file through `memory_ingest_file` left its previous chunk set behind, so a file that
///     re-chunks to a different count kept every index the new set did not overwrite and search
///     returned content the file no longer contains. The watch-digest path already
///     deleted-then-ingested; the direct path did not.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DirectIngestReplacesStaleChunksTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public DirectIngestReplacesStaleChunksTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), ignoreRulesProvider: new IgnoreRulesProvider(), modelMigrationLease: null, jsonChunker: null, noisePolicies: null, settings: null, codeChunker: null, measurements: null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private Task ScopeDataRootAsync() =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

    private async Task<IReadOnlyList<(int ChunkIndex, int TotalChunks, int Length)>> ChunksAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<(int, int, int)>(
            "SELECT chunk_index, total_chunks, length(value) FROM entries WHERE source_file = @path ORDER BY chunk_index",
            new { path });
        return rows.ToList();
    }

    private async Task<int> CodeChunkCountAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM code_entries WHERE path = @path",
            new { path = IngestPath.Normalize(path) });
    }

    /// <summary>Blank-line-separated blocks — one <see cref="StubCodeChunker" /> chunk per block.</summary>
    private static string CodeBlocks(int count, string prefix) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"class {prefix}{i}\n{{\n}}"));

    /// <summary>A five-section document; the markdown chunker splits it into more than one chunk.</summary>
    private static string LargeContent() =>
        string.Join("\n\n", Enumerable.Range(1, 5).Select(i =>
            $"## Section {i}\n\n" + string.Join(" ", Enumerable.Repeat($"magnetostrictive section {i} body text", 40))));

    /// <summary>B1 — the defect itself.</summary>
    [RetryFact]
    public async Task ReIngestingAPathThatChunksDifferently_LeavesOnlyTheNewChunkSet()
    {
        var file = Path.Combine(_dataRoot, "sextant.md");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(file, "a short single-chunk sextant note", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        (await ChunksAsync(file)).Count.ShouldBe(1, "precondition: the small file is one chunk");

        await File.WriteAllTextAsync(file, LargeContent(), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var chunks = await ChunksAsync(file);
        chunks.Count.ShouldBeGreaterThan(1, "the rewritten file chunks into several");
        chunks.Select(c => c.TotalChunks).Distinct().ShouldHaveSingleItem(
            "a stale chunk from the previous ingest would carry the old total_chunks");
        chunks.Select(c => c.ChunkIndex).ShouldBe(Enumerable.Range(0, chunks.Count),
            "indexes must be exactly the new set, with no survivor from the old one");
    }

    /// <summary>B3 — dedup must keep working; re-ingesting identical content must not duplicate.</summary>
    [RetryFact]
    public async Task ReIngestingIdenticalContent_DoesNotDuplicateRows()
    {
        var file = Path.Combine(_dataRoot, "stable.md");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(file, LargeContent(), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        var first = await ChunksAsync(file);

        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        (await ChunksAsync(file)).Count.ShouldBe(first.Count);
    }

    /// <summary>B2 — the delete is per file; a sibling in the same directory must survive.</summary>
    [RetryFact]
    public async Task ReIngestingOneFile_LeavesItsSiblingsAlone()
    {
        var target = Path.Combine(_dataRoot, "target.md");
        var sibling = Path.Combine(_dataRoot, "sibling.md");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(target, "magnetostrictive target note", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sibling, "magnetostrictive sibling note", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", target, null, TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", sibling, null, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(target, LargeContent(), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", target, null, TestContext.Current.CancellationToken);

        (await ChunksAsync(sibling)).Count.ShouldBe(1, "the sibling was never re-ingested and must be untouched");
    }

    /// <summary>
    ///     B4 — an ignored path ingests nothing AND loses whatever it had stored. Ignoring a file
    ///     is a statement that its content should not be searchable; leaving the previous chunks
    ///     behind keeps serving exactly the stale content this defect is about.
    /// </summary>
    [RetryFact]
    public async Task DirectIngestOfANowIgnoredFile_IngestsNothing_AndRemovesWhatWasStored()
    {
        var file = Path.Combine(_dataRoot, "ignored-later.md");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(file, "magnetostrictive content", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        (await ChunksAsync(file)).ShouldNotBeEmpty("precondition: ingested before it became ignored");

        await File.WriteAllTextAsync(Path.Combine(_dataRoot, "ai-raccoon.ignore"), "ignored-later.md\n",
            TestContext.Current.CancellationToken);
        var rows = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        rows.ShouldBe(0, "an ignored path ingests nothing");
        (await ChunksAsync(file)).ShouldBeEmpty(
            "ignoring a file must take its stored chunks with it — otherwise search keeps serving them");
        (await _store.SearchAsync(new SearchQuery("acme", "magnetostrictive"),
                TestContext.Current.CancellationToken)).Results
            .ShouldBeEmpty("the ignored file's content must no longer be reachable by search");
    }

    /// <summary>
    ///     B6 — `memory_ingest_directory` walks per file and must replace each walked file's chunk
    ///     set for the same reason the single-file path does; otherwise the defect just moves.
    /// </summary>
    [RetryFact]
    public async Task ReIngestingADirectory_ReplacesEachWalkedFilesChunkSet()
    {
        var dir = Path.Combine(_dataRoot, "docs");
        Directory.CreateDirectory(dir);
        var shrinking = Path.Combine(dir, "shrinking.md");
        var stable = Path.Combine(dir, "stable.md");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(shrinking, LargeContent(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(stable, "magnetostrictive stable note", TestContext.Current.CancellationToken);
        await _store.IngestDirectoryAsync("acme", dir, null, TestContext.Current.CancellationToken);
        (await ChunksAsync(shrinking)).Count.ShouldBeGreaterThan(1, "precondition: it starts multi-chunk");

        await File.WriteAllTextAsync(shrinking, "now a single short chunk", TestContext.Current.CancellationToken);
        await _store.IngestDirectoryAsync("acme", dir, null, TestContext.Current.CancellationToken);

        var chunks = await ChunksAsync(shrinking);
        chunks.Count.ShouldBe(1, "the previous multi-chunk set must not survive a directory re-ingest");
        chunks[0].TotalChunks.ShouldBe(1);
        (await ChunksAsync(stable)).Count.ShouldBe(1, "an unchanged sibling in the walk is untouched");
    }

    /// <summary>
    ///     #485 (docs/work/2026-08-23-post-delta-4-plan.md §WP2): the directory walk passed
    ///     `keepCode: null` for every walked file, so a code file that re-chunks to fewer blocks
    ///     through `memory_ingest_directory` stranded its old `code_entries` rows — the same defect
    ///     B6 fixed for the memory corpus, left open for code (single-file leg fixed by #481).
    /// </summary>
    [RetryFact]
    public async Task ReIngestingADirectory_PrunesStaleCodeEntries_ForAShrunkCodeFile()
    {
        var dir = Path.Combine(_dataRoot, "src");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Widget.cs");
        var codeStore = CreateCodeStore();
        await codeStore.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, CodeBlocks(5, "Old"), TestContext.Current.CancellationToken);
        await codeStore.IngestDirectoryAsync("acme", dir, null, TestContext.Current.CancellationToken);
        (await CodeChunkCountAsync(file)).ShouldBe(5, "precondition: five blank-line-separated blocks chunk to five rows");

        await File.WriteAllTextAsync(file, CodeBlocks(1, "New"), TestContext.Current.CancellationToken);
        await codeStore.IngestDirectoryAsync("acme", dir, null, TestContext.Current.CancellationToken);

        (await CodeChunkCountAsync(file)).ShouldBe(1,
            "the rewritten file chunks into one block; the other four stranded code_entries rows must not survive a directory re-ingest");
    }

    private SqliteMemoryStore CreateCodeStore() =>
        TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), ignoreRulesProvider: new IgnoreRulesProvider(),
            codeChunker: new StubCodeChunker(), modelMigrationLease: null, jsonChunker: null, noisePolicies: null, settings: null, measurements: null);
}
