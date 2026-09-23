using System.Text.Json;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     P1 (owner gate): the chunker a backfilled row gets follows its own <c>source_file</c>, not a
///     single fixed chunker. A memory_write note (missing source_file, or a citation whose path does
///     not name this row's own file — <c>source_file</c> is only a citation there) chunks as
///     markdown; a file row (<c>path == source_file</c>) chunks with the handler its extension owns,
///     or the plain-text fallback when no handler owns it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBackfillChunkerRoutingTests : IDisposable
{
    private const string ProjectId = "acme";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-backfill-routing");
    private readonly SqliteConnectionFactory _factory;

    public ChunkBackfillChunkerRoutingTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task OversizedTxtFileRow_ChunksWithThePlainTextHandler()
    {
        const string Path = "notes-t1.txt";
        var value = HeadingsAndFenceInput("t1");
        await using var connection = await OpenSeededAsync(Path, Path, value);
        var backfill = Backfill();
        var (budget, overlay, countTokens) = await backfill.BudgetAsync(connection, TestContext.Current.CancellationToken);
        var plainOutput = TestData.RealPlainTextChunker().Chunk(value, budget, overlay, countTokens);
        AssertChunkersDiffer(plainOutput, TestData.RealMarkdownChunker().Chunk(value, budget, overlay, countTokens));

        await backfill.RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        (await StoredPiecesAsync(connection, Path)).ShouldBe(plainOutput);
    }

    [RetryFact]
    public async Task OversizedJsonObjectFileRow_ChunksStructurally_WithTheJsonHandler()
    {
        const string Path = "config-t2.json";
        var value = LargeJsonObject("t2");
        await using var connection = await OpenSeededAsync(Path, Path, value);
        var backfill = Backfill();
        var (budget, overlay, countTokens) = await backfill.BudgetAsync(connection, TestContext.Current.CancellationToken);
        var jsonOutput = TestData.RealJsonChunker().Chunk(value, budget, overlay, countTokens);
        jsonOutput.Count.ShouldBeGreaterThan(1, "arrange: the JSON input must actually need more than one piece");
        AssertChunkersDiffer(jsonOutput, TestData.RealMarkdownChunker().Chunk(value, budget, overlay, countTokens));

        await backfill.RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        var pieces = await StoredPiecesAsync(connection, Path);
        pieces.ShouldBe(jsonOutput);
        pieces.ShouldAllBe(piece => IsValidJson(piece), "each structurally split piece must still parse as JSON on its own");
    }

    [RetryFact]
    public async Task NullSourceFileRow_ChunksAsANote_WithTheMarkdownHandler()
    {
        const string Path = "note-t4";
        var value = HeadingsAndFenceInput("t4");
        await using var connection = await OpenSeededAsync(Path, sourceFile: null, value: value);
        var backfill = Backfill();
        var (budget, overlay, countTokens) = await backfill.BudgetAsync(connection, TestContext.Current.CancellationToken);
        var markdownOutput = TestData.RealMarkdownChunker().Chunk(value, budget, overlay, countTokens);
        AssertChunkersDiffer(markdownOutput, TestData.RealPlainTextChunker().Chunk(value, budget, overlay, countTokens));

        await backfill.RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        (await StoredPiecesAsync(connection, Path)).ShouldBe(markdownOutput);
    }

    [RetryFact]
    public async Task UnownedExtensionFileRow_FallsBackToPlainText()
    {
        const string Path = "notes.log";
        var value = HeadingsAndFenceInput("t5");
        await using var connection = await OpenSeededAsync(Path, Path, value);
        var backfill = Backfill();
        var (budget, overlay, countTokens) = await backfill.BudgetAsync(connection, TestContext.Current.CancellationToken);
        var plainOutput = TestData.RealPlainTextChunker().Chunk(value, budget, overlay, countTokens);
        AssertChunkersDiffer(plainOutput, TestData.RealMarkdownChunker().Chunk(value, budget, overlay, countTokens));

        await backfill.RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        (await StoredPiecesAsync(connection, Path)).ShouldBe(plainOutput);
    }

    /// <summary>A memory_write row whose citation names a real file's path (here a <c>.cs</c> no
    /// handler owns) must still chunk as a note — routing by <c>source_file</c>'s extension alone,
    /// ignoring that <c>path != source_file</c>, would pick the plain-text fallback instead.</summary>
    [RetryFact]
    public async Task MemoryWriteShapedRow_PathDiffersFromSourceFile_ChunksAsANote()
    {
        const string Path = "note-t7";
        const string SourceFile = "src/Foo.cs";
        var value = HeadingsAndFenceInput("t7");
        await using var connection = await OpenSeededAsync(Path, SourceFile, value);
        var backfill = Backfill();
        var (budget, overlay, countTokens) = await backfill.BudgetAsync(connection, TestContext.Current.CancellationToken);
        var markdownOutput = TestData.RealMarkdownChunker().Chunk(value, budget, overlay, countTokens);
        AssertChunkersDiffer(markdownOutput, TestData.RealPlainTextChunker().Chunk(value, budget, overlay, countTokens));

        await backfill.RunAsync(connection, dryRun: false, TestContext.Current.CancellationToken);

        (await StoredPiecesAsync(connection, Path)).ShouldBe(markdownOutput);
    }

    private static void AssertChunkersDiffer(IReadOnlyList<string> expected, IReadOnlyList<string> other) =>
        expected.SequenceEqual(other).ShouldBeFalse(
            "arrange: the two chunkers must disagree on this input, or this test cannot go red");

    private static bool IsValidJson(string candidate)
    {
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private ChunkBackfill Backfill() =>
        new(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(), TestData.RealPlainTextChunker(),
            new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());

    private static string HeadingsAndFenceInput(string marker) =>
        $"# Heading One ({marker})\n\n" + Paragraphs(marker, 0, 30) +
        "\n\n```\ncode fence line one\ncode fence line two\n```\n\n" +
        $"## Heading Two ({marker})\n\n" + Paragraphs(marker, 30, 20);

    /// <summary>Every word is keyed by its own paragraph and slot index, so no two chunk boundaries
    /// the real chunker picks can ever land on byte-identical text — <c>InsertEntry</c> is ON
    /// CONFLICT DO NOTHING, and a repeated piece would silently drop instead of asserting.</summary>
    private static string Paragraphs(string marker, int startIndex, int count) =>
        string.Join("\n\n", Enumerable.Range(startIndex, count).Select(i =>
            $"Paragraph {marker}-{i}. " + string.Join(' ', Enumerable.Range(0, 12).Select(w => $"{marker}word{i}-{w}"))));

    private static string LargeJsonObject(string marker)
    {
        var props = Enumerable.Range(0, 40).Select(i =>
            $"  \"{marker}_field_{i}\": \"{string.Join(' ', Enumerable.Range(0, 8).Select(w => $"{marker}word{i}-{w}"))}\"");
        return "{\n" + string.Join(",\n", props) + "\n}";
    }

    private static async Task<List<string>> StoredPiecesAsync(SqliteConnection connection, string path) =>
        (await connection.QueryAsync<string>(
            "SELECT value FROM entries WHERE path = @path ORDER BY id", new { path })).ToList();

    /// <summary>Seeds one row the way the defect made it: a single over-window row, <paramref name="sourceFile"/>
    /// left null for a memory_write-shaped row.</summary>
    private async Task<SqliteConnection> OpenSeededAsync(string path, string? sourceFile, string value)
    {
        var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                 created_at, updated_at, embed_state, chunk_index, total_chunks)
            VALUES (@hash, @path, @value, @sourceFile, 'project', @projectId, 1, 1, 'embedded', 0, 1)
            """,
            new { hash = $"h-{path}", path, value, sourceFile, projectId = ProjectId });
        return connection;
    }
}
