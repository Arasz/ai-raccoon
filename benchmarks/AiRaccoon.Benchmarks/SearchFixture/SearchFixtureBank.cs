using System.Text;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Benchmarks.SearchFixture;

/// <summary>
///     Builds a realistic fixture bank for end-to-end search-latency benchmarking (WP4;
///     docs/plans/2026-08-08-search-knn-perf.md §5): ~2,000 project-scoped entries plus ~100
///     shared entries across 10 topics, roughly half grouped into 5-chunk source_file groups,
///     all embedded through the store's normal write/embed surface (AddContentAsync +
///     EmbedPendingAsync) against <see cref="FakeEmbeddingEndpoint" /> so vec_entries fills via
///     the real vec0 triggers. Synthetic, deterministic embeddings are used rather than the
///     bundled ONNX engine: corpus embedding cost is excluded from the timed benchmark either
///     way (it happens once here, at setup), and synthetic keeps the harness offline and fast to
///     build; the query vector each benchmark iteration embeds is drawn from the same
///     deterministic source, so the two are consistent.
/// </summary>
public sealed class SearchFixtureBank : IAsyncDisposable
{
    public const string ProjectId = "bench-project";

    private const int ProjectEntryCount = 2000;
    private const int SharedEntryCount = 100;
    private const int ChunkGroupSize = 5;
    private const int BlockSize = ChunkGroupSize * 2; // 5 grouped chunks + 5 standalone notes per block.
    private const int RandomSeed = 20260808;

    /// <summary>Fixed query set the benchmark rotates through; each phrase is also seeded verbatim into a share of the fixture content.</summary>
    public static readonly IReadOnlyList<string> Queries =
    [
        "database migration failure",
        "vector search latency regression",
        "authentication token refresh bug",
        "incident postmortem review process",
        "container image build pipeline",
        "customer support escalation workflow",
        "search index chunk maintenance",
        "embedding model configuration change",
        "workspace promotion and sharing flow",
        "sync conflict resolution strategy"
    ];

    private static readonly string[] FillerWords =
    [
        "system", "connection", "pool", "retry", "timeout", "latency", "query", "index", "cache",
        "schema", "migration", "trigger", "partition", "vector", "embedding", "search", "ranking",
        "snippet", "chunk", "context", "workspace", "project", "shared", "sync", "conflict",
        "resolution", "review", "incident", "pipeline", "build", "container", "deploy", "monitor",
        "alert", "threshold", "throughput", "concurrency", "transaction", "rollback", "commit"
    ];

    private static readonly IModelMigrationLease ModelMigrationLease = Substitute.For<IModelMigrationLease>();
    private static readonly TimeProvider TimeProvider = new FakeTimeProvider();

    private readonly string _dataRoot;
    private readonly FakeEmbeddingEndpoint _embeddings;
    private readonly SqliteConnectionFactory _factory;

    private SearchFixtureBank(string dataRoot, SqliteConnectionFactory factory, FakeEmbeddingEndpoint embeddings, SqliteMemoryStore store)
    {
        _dataRoot = dataRoot;
        _factory = factory;
        _embeddings = embeddings;
        Store = store;
    }

    public SqliteMemoryStore Store { get; }

    public async ValueTask DisposeAsync()
    {
        await _embeddings.DisposeAsync().ConfigureAwait(false);
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    public static async Task<SearchFixtureBank> BuildAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = Directory.CreateTempSubdirectory("ai-raccoon-search-bench").FullName;
        var options = new InfrastructureOptions { DataRoot = dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, new NoopEncryptionKeyResolver());
        var sourceStore = new SqliteMemorySourceStore(factory);
        var embeddingService = new EmbeddingService(NullLogger<EmbeddingService>.Instance, new LocalTokenizer());
        var countTokens = new TokenCount(new O200kTokenizer().CountTokens);
        var markdownChunker = new MarkdownChunker(countTokens);
        var fileTypeMatcher = new FileTypeMatcher([
            new MarkdownFileTypeHandler(markdownChunker),
            new JsonFileTypeHandler(new JsonFileTypeChunker(countTokens, markdownChunker, ChunkingDefaults.OverlayTokens))
        ]);
        var embedder = new EntryEmbedder(embeddingService, ModelMigrationLease, TimeProvider);
        var fileIngestor = new FileIngestor(fileTypeMatcher, embedder, sourceStore, TimeProvider.System, new LocalTokenizer());
        var noiseFilteringService = new NoiseFilteringService([]);
        var store = new SqliteMemoryStore(factory, sourceStore, fileIngestor, embedder, TimeProvider.System,
            NullLogger<SqliteMemoryStore>.Instance, noiseFilteringService, new SqliteSettingsStore(factory));

        // Rows land pending (no engine configured yet) so the fixture write loop pays for one
        // round trip per row, not one HTTP call per row; embedding happens once, batched, below.
        var random = new Random(RandomSeed);
        await WriteFixtureAsync(store, random, cancellationToken).ConfigureAwait(false);

        var embeddings = await FakeEmbeddingEndpoint.StartAsync(cancellationToken).ConfigureAwait(false);
        await store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "bench-fake-key", cancellationToken)
            .ConfigureAwait(false);
        await store.ConfigureEmbeddingAsync("openai", "bench-embed-model", embeddings.BaseUrl, cancellationToken)
            .ConfigureAwait(false);
        var embedded = await store.EmbedPendingAsync(ProjectId, null, cancellationToken).ConfigureAwait(false);
        if (embedded.Pending > 0)
        {
            throw new InvalidOperationException(
                $"Search fixture bank still has {embedded.Pending} pending rows after EmbedPendingAsync — the fixture would search a partially embedded bank.");
        }

        var bank = new SearchFixtureBank(dataRoot, factory, embeddings, store);
        await bank.VerifyAsync(cancellationToken).ConfigureAwait(false);
        return bank;
    }

    /// <summary>
    ///     The checks a broken fixture must fail: a benchmark over an empty, unsearchable, or
    ///     structure-blind bank silently measures nothing (WP4 TDD interpretation). The
    ///     vec_entries and probe-query assertions were proven to fail by temporarily skipping the
    ///     EmbedPendingAsync call above; the vec_structure assertion was proven to fail by
    ///     temporarily forcing every fixture chunk headingless (both proofs done during development).
    /// </summary>
    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var vectorRows = await Scalar(connection, "SELECT COUNT(*) FROM vec_entries", cancellationToken)
            .ConfigureAwait(false);
        if (vectorRows <= 0)
        {
            throw new InvalidOperationException(
                "Search fixture bank has 0 vec_entries rows after embedding — the benchmark would measure nothing.");
        }

        var structureRows = await Scalar(connection, "SELECT COUNT(*) FROM vec_structure", cancellationToken)
            .ConfigureAwait(false);
        if (structureRows <= 0)
        {
            throw new InvalidOperationException(
                "Search fixture bank has 0 vec_structure rows after embedding — the structure modality would measure nothing.");
        }

        var structuredEntries = await Scalar(connection,
                "SELECT COUNT(*) FROM entries WHERE structure_embedding IS NOT NULL", cancellationToken)
            .ConfigureAwait(false);
        var totalEntries = await Scalar(connection, "SELECT COUNT(*) FROM entries", cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine(
            $"[SearchFixtureBank] structure-populated fraction: {structuredEntries}/{totalEntries} " +
            $"({(double)structuredEntries / totalEntries:P1})");

        var probe = await Store.SearchAsync(
                new SearchQuery(ProjectId, Queries[0], Limit: 10, MinRelativeScore: 0.0),
                cancellationToken)
            .ConfigureAwait(false);
        if (probe.Results.Count == 0)
        {
            throw new InvalidOperationException(
                $"Search fixture bank returned 0 results for probe query '{Queries[0]}' — the benchmark would measure nothing.");
        }
    }

    private static Task<long> Scalar(SqliteConnection connection, string sql, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: cancellationToken));

    private static async Task WriteFixtureAsync(SqliteMemoryStore store, Random random, CancellationToken cancellationToken)
    {
        for (var topicIndex = 0; topicIndex < Queries.Count; topicIndex++)
        {
            await WriteTopicAsync(store, random, topicIndex, ProjectEntryCount / Queries.Count,
                ContextNaming.ProjectContext(ProjectId), "project", cancellationToken).ConfigureAwait(false);
            await WriteTopicAsync(store, random, topicIndex, SharedEntryCount / Queries.Count,
                ContextNaming.SharedContext, "shared", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteTopicAsync(SqliteMemoryStore store, Random random, int topicIndex, int count,
        string context, string scopeTag, CancellationToken cancellationToken)
    {
        var topic = Queries[topicIndex];
        for (var i = 0; i < count; i++)
        {
            var positionInBlock = i % BlockSize;
            var blockIndex = i / BlockSize;
            var grouped = positionInBlock < ChunkGroupSize;
            var sourceFile = grouped ? $"docs/{scopeTag}/topic-{topicIndex}-block-{blockIndex}.md" : null;

            var path = $"bench/{scopeTag}/topic-{topicIndex}/doc-{i:D5}.md";
            var content = ContentFor(topic, random, grouped, positionInBlock);
            await store.AddContentAsync(ProjectId, path, content, context, sourceFile,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     A few-hundred-char paragraph: the topic phrase followed by 3-5 filler sentences. A
    ///     grouped chunk (part of a 5-chunk source_file block) also carries an H1/H2 heading pair,
    ///     mirroring a real ingested markdown doc's structure; a standalone note stays plain — a
    ///     realistic mix rather than an all-headed or all-headingless corpus.
    /// </summary>
    private static string ContentFor(string topic, Random random, bool grouped, int sectionIndex)
    {
        var builder = new StringBuilder();
        if (grouped)
        {
            builder.Append("# ").Append(char.ToUpperInvariant(topic[0])).Append(topic.AsSpan(1)).Append('\n')
                .Append("## Section ").Append(sectionIndex + 1).Append("\n\n");
        }

        builder.Append(char.ToUpperInvariant(topic[0])).Append(topic.AsSpan(1)).Append(". ");

        var sentenceCount = random.Next(3, 6);
        for (var s = 0; s < sentenceCount; s++)
        {
            builder.Append(SentenceFrom(random)).Append(' ');
        }

        return builder.ToString().TrimEnd();
    }

    private static string SentenceFrom(Random random)
    {
        var wordCount = random.Next(8, 16);
        var words = new string[wordCount];
        for (var w = 0; w < wordCount; w++)
        {
            words[w] = FillerWords[random.Next(FillerWords.Length)];
        }

        var sentence = string.Join(' ', words);
        return $"{char.ToUpperInvariant(sentence[0])}{sentence[1..]}.";
    }

    private sealed class NoopEncryptionKeyResolver : IEncryptionKeyResolver
    {
        public Task<ResolvedKey> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(ResolvedKey.None);
    }
}
