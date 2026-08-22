using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     #436: `DirectIngestReplacesStaleChunksTests` fixed Defect B for the memory corpus
///     (`entries`); `ICodeIngestor` never reported its chunk hashes, so the same defect stranded
///     `code_entries` rows a re-ingest's smaller chunk set no longer covers. Owner ruling on #436:
///     the fix must key on `FileIngestor`'s `FingerprintEligible`/whitespace-only distinction —
///     never read a stand-in chunker's zero chunks as "delete everything" (S3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeIngestReplacesStaleChunksTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-prune");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public CodeIngestReplacesStaleChunksTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = CreateStore(new StubCodeChunker());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private SqliteMemoryStore CreateStore(AiRaccoon.Core.Chunking.ICodeChunker codeChunker) =>
        TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), ignoreRulesProvider: new IgnoreRulesProvider(), codeChunker: codeChunker);

    private Task ScopeDataRootAsync() =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

    private async Task<IReadOnlyList<(int ChunkIndex, int TotalChunks, string Hash)>> CodeChunksAsync(string path)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<(int, int, string)>(
            "SELECT chunk_index, total_chunks, hash FROM code_entries WHERE path = @path ORDER BY chunk_index",
            new { path = IngestPath.Normalize(path) });
        return rows.ToList();
    }

    /// <summary>Blank-line-separated blocks — one <see cref="StubCodeChunker" /> chunk per block.</summary>
    private static string CodeBlocks(int count, string prefix) =>
        string.Join("\n\n", Enumerable.Range(1, count).Select(i => $"class {prefix}{i}\n{{\n}}"));

    /// <summary>B1 — the defect itself, mirrored for the code corpus.</summary>
    [Fact]
    public async Task ReIngestingACodeFileThatChunksToFewerBlocks_LeavesOnlyTheNewChunkSet()
    {
        var file = Path.Combine(_dataRoot, "Widget.cs");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(file, CodeBlocks(5, "Old"), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        (await CodeChunksAsync(file)).Count.ShouldBe(5, "precondition: five blank-line-separated blocks chunk to five rows");

        await File.WriteAllTextAsync(file, CodeBlocks(2, "New"), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var chunks = await CodeChunksAsync(file);
        chunks.Count.ShouldBe(2, "the rewritten file chunks into two blocks; the other three must not survive");
        chunks.Select(c => c.TotalChunks).Distinct().ShouldHaveSingleItem(
            "a stale chunk from the previous ingest would carry the old total_chunks");
        chunks.Select(c => c.ChunkIndex).ShouldBe(Enumerable.Range(0, chunks.Count),
            "indexes must be exactly the new set, with no survivor from the old one");
    }

    /// <summary>B2 — the delete is per file; a sibling code file must survive a single-file re-ingest.</summary>
    [Fact]
    public async Task ReIngestingOneCodeFile_LeavesItsSiblingAlone()
    {
        var target = Path.Combine(_dataRoot, "Target.cs");
        var sibling = Path.Combine(_dataRoot, "Sibling.cs");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(target, CodeBlocks(3, "TargetOld"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(sibling, CodeBlocks(3, "Sibling"), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", target, null, TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", sibling, null, TestContext.Current.CancellationToken);
        (await CodeChunksAsync(sibling)).Count.ShouldBe(3, "precondition");

        await File.WriteAllTextAsync(target, CodeBlocks(1, "TargetNew"), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", target, null, TestContext.Current.CancellationToken);

        (await CodeChunksAsync(sibling)).Count.ShouldBe(3, "the sibling was never re-ingested and must be untouched");
    }

    /// <summary>
    ///     S3 guard — a non-empty code file whose chunker returns zero rows (a stand-in like
    ///     `NoOpCodeChunker`, standing in for the engine-blocked real chunker) must NOT be read as
    ///     "no chunks, delete everything": its existing rows must survive the re-ingest attempt.
    ///     This is the way the #436 fix goes wrong if `FileIngestor`'s zero-chunk/whitespace-only
    ///     distinction (S3 vs B1) is not respected by the prune leg.
    /// </summary>
    [Fact]
    public async Task ReIngestingWithAZeroChunkStandInChunker_DoesNotDeleteExistingCodeRows()
    {
        var file = Path.Combine(_dataRoot, "Widget.cs");
        await ScopeDataRootAsync();
        await File.WriteAllTextAsync(file, CodeBlocks(3, "Kept"), TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        var before = await CodeChunksAsync(file);
        before.Count.ShouldBe(3, "precondition: a real chunker produced three rows");

        // Simulate the code engine going unavailable mid-project: the same bank, but a store
        // wired with the zero-chunk stand-in chunker instead of the real one.
        var noOpStore = CreateStore(new NoOpCodeChunker(NullLogger<NoOpCodeChunker>.Instance));
        await File.WriteAllTextAsync(file, CodeBlocks(3, "NotChunked"), TestContext.Current.CancellationToken);
        var rows = await noOpStore.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        rows.ShouldBe(0, "the stand-in chunker inserts nothing");
        var after = await CodeChunksAsync(file);
        after.Count.ShouldBe(3, "the previous, still-valid rows must survive a zero-chunk stand-in re-ingest");
        after.Select(c => c.Hash).ShouldBe(before.Select(c => c.Hash), ignoreOrder: true,
            "not just the same count — the exact same rows, untouched");
    }
}
