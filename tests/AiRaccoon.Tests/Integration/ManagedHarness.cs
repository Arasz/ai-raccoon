using System.Diagnostics;
using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     The new side of the FR-NM-5 (see docs/work/features-native-memory/native-memory.feature) parity gate: the managed store (FTS5 + vec0 fused with
///     RRF) loaded with the shared corpus and queried through the SweepRunner's RankSource
///     plug point. Same corpus, one project context, the bundled int8 ONNX engine — the
///     measured counterpart to the vendored sqlite-memory reference oracle.
/// </summary>
public sealed class ManagedHarness
{
    public const string ProjectId = "harness";

    /// <summary>Largest sweep k; each query is ranked to this depth so every metric window is covered.</summary>
    public const int MaxRankedK = 60;

    public static readonly SweepPoint DefaultPoint = new(60, 1, 1);

    private readonly string _dataRoot;
    private readonly List<double> _queryLatenciesMs = [];

    private ManagedHarness(string dataRoot, SqliteMemoryStore store)
    {
        _dataRoot = dataRoot;
        Store = store;
    }

    public SqliteMemoryStore Store { get; }

    public IReadOnlyList<double> QueryLatenciesMs => _queryLatenciesMs;

    /// <summary>Rows committed for the corpus project; the parity gate asserts it matches the corpus size.</summary>
    public int DocumentCount { get; private set; }

    public static async Task<ManagedHarness> BuildAsync(CancellationToken cancellationToken = default)
    {
        var ensured = await BundledModel.EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        var dataRoot = TestData.CreateTempRoot("ai-raccoon-parity");
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        var store = new SqliteMemoryStore(factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)),
            new TokenizerChunker(), new EmbeddingService());

        await store.ConfigureEmbeddingAsync("local", null, null, cancellationToken)
            .ConfigureAwait(false);

        foreach (var doc in RealWorldCorpus.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.AddContentAsync(ProjectId, doc.Id, doc.Text, ContextNaming.ProjectContext(ProjectId),
                cancellationToken).ConfigureAwait(false);
        }

        var harness = new ManagedHarness(dataRoot, store)
        {
            DocumentCount = (await store.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
                cancellationToken).ConfigureAwait(false)).Count
        };
        return harness;
    }

    /// <summary>SweepRunner RankSource over the managed store; ranks by result path (doc id).</summary>
    public async Task<IReadOnlyList<string>> RankAsync(
        SweepPoint point, CorpusQuery query, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var results = await Store.SearchAsync(new SearchQuery(
                ProjectId,
                query.Text,
                SearchScope.Project,
                Limit: MaxRankedK,
                MinScore: 0.0,
                RrfK: point.K,
                FtsWeight: point.FtsWeight,
                VectorWeight: point.VectorWeight), cancellationToken).ConfigureAwait(false);
            return [.. results.Select(result => result.Path)];
        }
        finally
        {
            stopwatch.Stop();
            lock (_queryLatenciesMs)
            {
                _queryLatenciesMs.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Builds the managed harness once per test collection; the corpus load is the expensive step.</summary>
public sealed class ManagedHarnessFixture : IAsyncLifetime
{
    public ManagedHarness Harness { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Harness = await ManagedHarness.BuildAsync();

    public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
}

[CollectionDefinition("managed-parity")]
public sealed class ManagedParityCollection : ICollectionFixture<ManagedHarnessFixture>
{
}
