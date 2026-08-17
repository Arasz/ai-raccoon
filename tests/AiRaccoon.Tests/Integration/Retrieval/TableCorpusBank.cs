using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     Builds a bank from the vendored table corpus at test time, through the production ingest path.
///     Unlike the committed jsaa fixture (docs/adr/0077: "the gate goes green having measured
///     nothing"), this re-chunks on every run, so a chunking change moves the numbers scored off it.
/// </summary>
internal sealed class TableCorpusBank : IAsyncDisposable
{
    public const string ProjectId = "table-corpus";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    private TableCorpusBank(string dataRoot, SqliteConnectionFactory factory, SqliteMemoryStore store,
        IReadOnlyList<CorpusChunk> chunks, int maxTokens)
    {
        _dataRoot = dataRoot;
        _factory = factory;
        _store = store;
        Chunks = chunks;
        MaxTokens = maxTokens;
    }

    /// <summary>Every chunk the ingest produced, as the span-anchored relevance predicate reads them.</summary>
    public IReadOnlyList<CorpusChunk> Chunks { get; }

    /// <summary>The chunk budget this bank was built at — the axis a chunking arm moves.</summary>
    public int MaxTokens { get; }

    /// <summary>
    ///     Ingests the corpus at <paramref name="maxTokensOverride" /> tokens per chunk, or at the
    ///     production budget when null. The override is how a test perturbs chunking without touching
    ///     production configuration.
    /// </summary>
    public static async Task<TableCorpusBank> BuildAsync(int? maxTokensOverride, CancellationToken cancellationToken)
    {
        var dataRoot = TestData.CreateTempRoot("ai-raccoon-table-corpus");
        var bert = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        // Mirrors FileIngestor.ChunkSizeForAsync for an unconfigured bank (docs/adr/0063).
        var productionMaxTokens = Math.Min(256, EmbeddingService.SafeChunkBudgetFor("local", null));
        var maxTokens = maxTokensOverride ?? productionMaxTokens;
        IMarkdownChunker chunker = new MarkdownChunker(text => bert.CountTokens(text));
        if (maxTokensOverride is not null)
        {
            chunker = new RebudgetedChunker(chunker, maxTokensOverride.Value);
        }

        var options = TestData.CreateInfrastructureOptions(dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), chunker, new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());

        var ensured = await TestData.CreateBundledModel().EnsureAsync(cancellationToken);
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        await store.ConfigureEmbeddingAsync("local", null, null, cancellationToken);

        var corpusRoot = TableCorpusCatalog.CorpusRoot();
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject(ProjectId),
            IngestScopeKeys.Serialize([corpusRoot]), cancellationToken);
        await store.IngestDirectoryAsync(ProjectId, corpusRoot, null, cancellationToken);

        var pending = await store.EmbedPendingAsync(ProjectId, null, cancellationToken);
        if (pending.Pending != 0)
        {
            throw new InvalidOperationException(
                $"table corpus left {pending.Pending} rows unembedded; scoring them would measure the backlog, not the ranking");
        }

        var chunks = await ReadChunksAsync(factory, cancellationToken);
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException($"table corpus ingest produced no chunks from {corpusRoot}");
        }

        return new TableCorpusBank(dataRoot, factory, store, chunks, maxTokens);
    }

    /// <summary>The ranked chunk hashes the production search path returns for a query.</summary>
    public async Task<IReadOnlyList<string>> RankAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var results = await _store.SearchAsync(
            new SearchQuery(ProjectId, query, SearchScope.Project, Limit: limit, MinRelativeScore: 0.0),
            cancellationToken);
        return [.. results.Results.Select(result => result.Hash)];
    }

    /// <summary>The relevance set for a graded query: chunks of its document that carry its answer span.</summary>
    public IReadOnlySet<string> RelevantFor(TableQuery query) =>
        SpanAnchoredRelevance.Resolve(Chunks, query.ExpectedSource, query.AnswerSpan);

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    private static async Task<IReadOnlyList<CorpusChunk>> ReadChunksAsync(
        SqliteConnectionFactory factory, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken);
        var rows = await connection.QueryAsync<CorpusChunk>(new CommandDefinition(
            "SELECT hash AS Hash, source_file AS SourceFile, value AS Value FROM entries " +
            "WHERE source_file IS NOT NULL ORDER BY source_file, chunk_index",
            cancellationToken: cancellationToken));
        return [.. rows];
    }

    /// <summary>Forces a chunk budget regardless of the one the ingest computed, so a test can compare
    /// two chunkings of the same documents through the same production path.</summary>
    private sealed class RebudgetedChunker(IMarkdownChunker inner, int maxTokens) : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int _, int overlayTokens = 0, TokenCount? countTokens = null) =>
            inner.Chunk(text, maxTokens, Math.Min(overlayTokens, maxTokens - 1), countTokens);
    }
}
