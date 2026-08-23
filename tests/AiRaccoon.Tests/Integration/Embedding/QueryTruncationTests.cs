using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     A search query longer than the model's window was silently cut to 256 tokens inside the
///     generator, and reported through the same event as a stored chunk — with a message calling it
///     a "Chunk". Two consequences, both live: nobody could tell a truncated query from a truncated
///     entry, and WP3's acceptance criterion ("EventId 414 is zero") could never reach zero because
///     queries share the event.
///     <para>
///         The query path now trims deliberately, before the generator sees the text, and says so in
///         its own words. 414 becomes what it always claimed to be: stored content only.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class QueryTruncationTests : IDisposable
{
    private const int QueryTrimmedEventId = 426; // was 416 until #466, then 418 until #522 review; both retired, not reused
    private const int ChunkTruncatedEventId = 414;

    private readonly string _dataRoot = TestData.CreateTempRoot("query-truncation");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<EmbeddingService> _logger = new();
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly FakeTimeProvider _timeProvider = new();

    public QueryTruncationTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ALongQuery_IsTrimmedByTheQueryPath_NotByTheGenerator()
    {
        await using var connection = await OpenConfiguredAsync();
        var embedder = new EntryEmbedder(new EmbeddingService(_logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(), new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())), _modelMigrationLease, _timeProvider);

        var vector = await embedder.EmbedQueryAsync(connection, LongQuery(), TestContext.Current.CancellationToken);

        vector.ShouldNotBeNull("a long query must still search, not fail");
        var records = _logger.Collector.GetSnapshot();
        records.ShouldContain(r => r.Id.Id == QueryTrimmedEventId,
            "the query path must report its own trimming");
        records.ShouldNotContain(r => r.Id.Id == ChunkTruncatedEventId,
            "414 means a stored entry was truncated; a query reaching it is what made that event unmeasurable");
    }

    /// <summary>The message is for a person deciding whether their search was answered properly.</summary>
    [Fact]
    public async Task TheQueryMessage_SaysWhatWasCutAndWhatItMeans()
    {
        await using var connection = await OpenConfiguredAsync();
        var embedder = new EntryEmbedder(new EmbeddingService(_logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(), new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())), _modelMigrationLease, _timeProvider);

        await embedder.EmbedQueryAsync(connection, LongQuery(), TestContext.Current.CancellationToken);

        var message = _logger.Collector.GetSnapshot().First(r => r.Id.Id == QueryTrimmedEventId).Message;
        message.ShouldContain("search query");
        message.ShouldContain("254");
        message.ShouldContain("shorter");
    }

    [Fact]
    public async Task AShortQuery_IsNotTrimmedAndSaysNothing()
    {
        await using var connection = await OpenConfiguredAsync();
        var embedder = new EntryEmbedder(new EmbeddingService(_logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(), new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())), _modelMigrationLease, _timeProvider);

        await embedder.EmbedQueryAsync(connection, "how does the promotion queue decide?",
            TestContext.Current.CancellationToken);

        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == QueryTrimmedEventId);
    }

    /// <summary>A query at the window's edge must not be trimmed — an off-by-one here silently drops a token.</summary>
    [Fact]
    public async Task AQueryExactlyAtTheWindow_IsNotTrimmed()
    {
        await using var connection = await OpenConfiguredAsync();
        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        var atLimit = TokenBudget.Trim(LongQuery(),
            OnnxEmbeddingGenerator.MaxContentTokens, text => tokenizer.CountTokens(text));
        tokenizer.CountTokens(atLimit).ShouldBe(OnnxEmbeddingGenerator.MaxContentTokens,
            "the fixture must sit exactly on the limit, or this tests nothing");
        var embedder = new EntryEmbedder(new EmbeddingService(_logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(), new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())), _modelMigrationLease, _timeProvider);

        await embedder.EmbedQueryAsync(connection, atLimit, TestContext.Current.CancellationToken);

        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == QueryTrimmedEventId);
    }

    private static string LongQuery() =>
        string.Join(' ', Enumerable.Repeat(
            "how does the retrieval pipeline weigh full text against vectors when the corpus is large", 40));

    private async Task<SqliteConnection> OpenConfiguredAsync()
    {
        var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, 'local')",
            new { key = EmbeddingSettingsKeys.Provider });
        return connection;
    }
}
