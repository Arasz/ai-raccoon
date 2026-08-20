using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     A write longer than one chunk must embed the chunk it stored, not the whole document.
///     <para>
///         Found in production by the message ADR-0071 rewrote: event 414 fired with
///         <c>818 tokens exceeded the 256-token window</c> on a live server, while the bank held
///         <b>zero</b> rows over the window. Both facts were true — <c>WriteAsync</c> chunked the
///         text and then embedded <c>request.Content</c>, so the row's text was in budget and its
///         vector was built from the entire document and truncated.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WriteEmbedsTheChunkNotTheDocumentTests : IDisposable
{
    private const string ProjectId = "acme";

    private readonly string _dataRoot = TestData.CreateTempRoot("embed-chunk-not-doc");
    private readonly CountingEmbeddingService _embeddings = new();
    private readonly SqliteConnectionFactory _factory;
    private readonly IMemoryStore _store;

    public WriteEmbedsTheChunkNotTheDocumentTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero)), _embeddings);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task AMultiChunkWrite_EmbedsEachStoredChunk_AndNeverTheWholeDocument()
    {
        await ConfigureLocalAsync();
        var document = Document(60);

        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, document),
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var stored = (await connection.QueryAsync<string>(
            "SELECT value FROM entries WHERE project_id = @p ORDER BY chunk_index", new { p = ProjectId })).ToList();
        stored.Count.ShouldBeGreaterThan(1, "the fixture must actually split, or this proves nothing");

        _embeddings.Calls.ShouldNotContain(inputs => inputs.Contains(document),
            "the whole document must never reach the embedder — its vector would be the document "
            + "truncated to the window, not the chunk that row actually stores");
    }

    /// <summary>
    ///     The sharp form: every text that reaches the embedder is one of the chunks actually
    ///     stored. Asserted this way rather than by picking "the first chunk" — a write with no
    ///     source_file never has chunk_index recomputed, so ordering by it returns an arbitrary row
    ///     and the test would be asserting against the wrong chunk.
    /// </summary>
    [Fact]
    public async Task EveryEmbeddedText_IsOneOfTheStoredChunks()
    {
        await ConfigureLocalAsync();

        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, Document(60)),
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var stored = (await connection.QueryAsync<string>(
            "SELECT value FROM entries WHERE project_id = @p", new { p = ProjectId })).ToHashSet(StringComparer.Ordinal);
        stored.Count.ShouldBeGreaterThan(1, "the fixture must actually split, or this proves nothing");

        var embeddedContent = _embeddings.Calls.SelectMany(inputs => inputs)
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        embeddedContent.ShouldNotBeEmpty("the write must embed something, or the assertion below is vacuous");
        embeddedContent.Where(text => !stored.Contains(text))
            .ShouldBeEmpty("every embedded text must be a stored chunk — anything else means a vector "
                           + "was built from text no row holds");
    }

    /// <summary>A single-chunk write is unchanged: content and chunk are the same string.</summary>
    [Fact]
    public async Task ASingleChunkWrite_StillEmbedsItsContent()
    {
        await ConfigureLocalAsync();
        const string Short = "a short fact that fits in one chunk";

        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, Short),
            TestContext.Current.CancellationToken);

        _embeddings.Calls.ShouldContain(inputs => inputs.Contains(Short));
    }

    private static string Document(int paragraphs) =>
        string.Join("\n\n", Enumerable.Range(0, paragraphs).Select(i =>
            $"Paragraph {i}. " + string.Join(' ', Enumerable.Repeat("memory retrieval budget window", 12))));

    private async Task ConfigureLocalAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, 'local')",
            new { key = EmbeddingSettingsKeys.Provider });
    }
}
