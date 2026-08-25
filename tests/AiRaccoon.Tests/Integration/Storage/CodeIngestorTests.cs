using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP3 (docs/work/2026-08-21-code-search-implementation-plan.md §3.4, QA WP3-T04…T09):
///     `CodeIngestor` scope/hidden/dedup/position-refresh behavior, driven by a fake chunker (the
///     real `CodeChunker` is engine-blocked — WP2, Wave 3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeIngestorTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _conn;
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-ingest");
    private readonly SqliteConnectionFactory _factory;
    private readonly CodeIngestor _ingestor;

    public CodeIngestorTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _conn = _factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();
        _ingestor = new CodeIngestor(new CodeFileTypeMatcher(), new StubCodeChunker(), new FakeTimeProvider(FixedNow));
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    [RetryFact]
    public async Task IngestFileAsync_OutsideScope_IsRefused_NoCodeRowsNoFingerprint()
    {
        var outside = TestData.CreateTempRoot("airaccoon-code-outside");
        try
        {
            var file = await WriteFileAsync(outside, "x.cs", "class X { }");

            await Should.ThrowAsync<PathOutsideScopeException>(() =>
                _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken));

            (await CountCodeEntriesAsync(file)).ShouldBe(0);
        }
        finally
        {
            TestData.DeleteTempRoot(outside);
        }
    }

    /// <summary>
    ///     S12: the extension check runs BEFORE the (DB-backed) scope check — a file that is never
    ///     going to produce a row (wrong extension) must not pay for, or fail on, a scope lookup.
    ///     Contrast with <see cref="IngestFileAsync_OutsideScope_IsRefused_NoCodeRowsNoFingerprint" />
    ///     above: that file IS a `.cs` file, so scope enforcement still applies to it.
    /// </summary>
    [RetryFact]
    public async Task IngestFileAsync_OutsideScope_NonCodeExtension_ReturnsZero_NoThrow()
    {
        var outside = TestData.CreateTempRoot("airaccoon-code-outside-noncode");
        try
        {
            var file = await WriteFileAsync(outside, "README.md", "# hello\n");

            var result = await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);

            result.Rows.ShouldBe(0);
            (await CountCodeEntriesAsync(file)).ShouldBe(0);
        }
        finally
        {
            TestData.DeleteTempRoot(outside);
        }
    }

    /// <summary>
    ///     S12: a caller that already resolved scope once (matching <c>FileIngestor.IngestDirectoryAsync</c>'s
    ///     once-per-walk read) can pass it in and skip the per-file DB re-read entirely — proved here
    ///     by NOT seeding any scope setting row at all: the internal read would find none and refuse
    ///     the file, so success here means the explicit <paramref name="scope"/> argument was used
    ///     instead of a fresh internal lookup.
    /// </summary>
    [RetryFact]
    public async Task IngestFileAsync_WithExplicitScope_SkipsInternalScopeRead()
    {
        var file = await WriteFileAsync(_dataRoot, "Program.cs", "class Program\n{\n}\n");

        var result = await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken,
            scope: [_dataRoot]);

        result.Rows.ShouldBe(1);
        (await CountCodeEntriesAsync(file)).ShouldBeGreaterThan(0);
    }

    [RetryFact]
    public async Task IngestFileAsync_InScope_InsertsPendingRows()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, "Program.cs", "class Program\n{\n}\n");

        var result = await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);

        result.Rows.ShouldBe(1);
        var rows = await CodeRowsAsync(file);
        rows.ShouldNotBeEmpty();
        ((bool)rows.All(r => (string)r.embed_state == "pending" && r.embedding == null)).ShouldBeTrue();
    }

    [RetryFact]
    public async Task IngestFileAsync_NonCodeExtension_ReturnsZero_NoRows()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, "README.md", "# hello\n");

        var result = await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);

        result.Rows.ShouldBe(0);
        (await CountCodeEntriesAsync(file)).ShouldBe(0);
    }

    [RetryFact]
    public async Task IngestFileAsync_HiddenFile_ReturnsZero_NoRows()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, ".hidden.cs", "class X { }");

        var result = await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);

        result.Rows.ShouldBe(0);
        (await CountCodeEntriesAsync(file)).ShouldBe(0);
    }

    [RetryFact]
    public async Task Reingest_SameContent_IsIdempotent_NoDuplicateRows()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, "A.cs", "class A\n{\n}\n\nclass B\n{\n}\n");

        await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);
        var firstCount = await CountCodeEntriesAsync(file);

        await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);
        var secondCount = await CountCodeEntriesAsync(file);

        firstCount.ShouldBeGreaterThan(0);
        secondCount.ShouldBe(firstCount);
    }

    [RetryFact]
    public async Task Reingest_ChangedContent_ReplacesOldChunks()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, "A.cs", "class A\n{\n}\n");
        await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);
        var oldHashes = (await CodeRowsAsync(file)).Select(r => (string)r.hash).ToArray();

        await File.WriteAllTextAsync(file, "class Completely\n{\n}\n\nclass Different\n{\n}\n",
            TestContext.Current.CancellationToken);
        // CodeIngestor dedups by (project, path, hash); a changed file's caller (digest/replace)
        // deletes the old rows first — this test exercises the ingest-then-delete-then-reingest
        // sequence a caller performs, proving no stale hash survives once its row is gone.
        await _conn.ExecuteAsync("DELETE FROM code_entries WHERE path = @file", new { file });
        await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);

        var newRows = await CodeRowsAsync(file);
        newRows.ShouldNotBeEmpty();
        newRows.Select(r => (string)r.hash).ShouldNotContain(h => oldHashes.Contains(h));
        var indices = newRows.Select(r => (int)r.chunk_index).OrderBy(i => i).ToArray();
        indices.ShouldBe(Enumerable.Range(0, newRows.Count).ToArray());
    }

    [RetryFact]
    public async Task Reingest_FileGainsLeadingLines_RefreshesPosition()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, "A.cs", "class A\n{\n}\n");
        await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);
        var before = (await CodeRowsAsync(file)).Single();
        ((int)before.line_start).ShouldBe(1);

        // Same chunk text (same hash), but now starting further down the file (leading blank
        // lines only, so the stub still emits exactly one chunk).
        await File.WriteAllTextAsync(file, "\n\nclass A\n{\n}\n",
            TestContext.Current.CancellationToken);
        await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);

        var after = (await CodeRowsAsync(file)).Single();
        ((int)after.id).ShouldBe((int)before.id, "dedup rediscovery updates the existing row, not a new one");
        ((int)after.line_start).ShouldBe(3);
    }

    [RetryFact]
    public async Task CodeRows_StoreNormalizedPathAndSourceFileWithLineRanges()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, "A.cs", "class A\n{\n}\n\nclass B\n{\n}\n");

        await _ingestor.IngestFileAsync(_conn, "acme", file, TestContext.Current.CancellationToken);

        var rows = (await CodeRowsAsync(file)).OrderBy(r => (int)r.chunk_index).ToList();
        rows.Count.ShouldBe(2);
        var expected = new StubCodeChunker().Chunk(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        for (var i = 0; i < rows.Count; i++)
        {
            ((string)rows[i].path).ShouldBe(IngestPath.Normalize(file));
            ((string)rows[i].source_file).ShouldBe(IngestPath.Normalize(file));
            ((int)rows[i].line_start).ShouldBe(expected[i].LineStart);
            ((int)rows[i].line_end).ShouldBe(expected[i].LineEnd);
        }
    }

    [RetryFact]
    public async Task IngestFileAsync_ConcurrentSameFile_SingleChunkSetNoDuplicateRows()
    {
        await AllowScopeAsync(_dataRoot);
        var file = await WriteFileAsync(_dataRoot, "A.cs", "class A\n{\n}\n\nclass B\n{\n}\n\nclass C\n{\n}\n");

        await using var connA = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await using var connB = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var barrier = new Barrier(2);
        var tasks = new[]
        {
            Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await _ingestor.IngestFileAsync(connA, "acme", file, TestContext.Current.CancellationToken);
            }),
            Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await _ingestor.IngestFileAsync(connB, "acme", file, TestContext.Current.CancellationToken);
            })
        };
        await Task.WhenAll(tasks);

        var rows = await CodeRowsAsync(file);
        rows.Count.ShouldBe(3, "one ingest's chunk set, not two");
        rows.Select(r => (string)r.hash).Distinct().Count().ShouldBe(3);
    }

    private async Task<string> WriteFileAsync(string root, string name, string content)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private Task AllowScopeAsync(string root) =>
        _conn.ExecuteAsync("INSERT INTO settings (key, value) VALUES (@key, @value)",
            new { key = IngestScopeKeys.ScopeGlobal, value = IngestScopeKeys.Serialize([root]) });

    private async Task<int> CountCodeEntriesAsync(string path) =>
        await _conn.ExecuteScalarAsync<int>("SELECT count(*) FROM code_entries WHERE path = @path",
            new { path = IngestPath.Normalize(path) });

    private async Task<List<dynamic>> CodeRowsAsync(string path)
    {
        var rows = await _conn.QueryAsync<dynamic>("SELECT * FROM code_entries WHERE path = @path",
            new { path = IngestPath.Normalize(path) });
        return rows.ToList();
    }
}
