using System.Diagnostics;
using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     The managed side of the native-memory parity gate (docs/work/features-native-memory/native-memory.feature):
///     the FTS5+vec0 store, fused with RRF, queried via SweepRunner's RankSource plug point over the
///     bundled int8 ONNX engine — the measured counterpart to the vendored sqlite-memory reference oracle.
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
        var ensured = await TestData.CreateBundledModel().EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        var dataRoot = TestData.CreateTempRoot("ai-raccoon-parity");
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(),
            clock, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

        await TestData.ConfigureAndDrainEmbeddingAsync(store, factory, TestData.CreateEmbeddingService(),
            "local", null, null, cancellationToken, clock).ConfigureAwait(false);

        foreach (var doc in RealWorldCorpus.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // sourceFile: each corpus document is inserted as exactly one row (AddContentAsync
            // does not chunk), so no two rows can ever be true chunk-siblings regardless of the
            // value chosen here — but the doc's own id at least stops SourceFile from being
            // uniformly null, which previously excluded every candidate from
            // SourceAffinityRanker's per-source grouping/consolidation logic entirely
            // (docs/reviews/2026-08-14-moe-codebase-review.md RAG-F8).
            await store.AddContentAsync(ProjectId, doc.Id, doc.Text, ContextNaming.ProjectContext(ProjectId),
                sourceFile: doc.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            var results = (await Store.SearchAsync(new SearchQuery(
                ProjectId,
                query.Text,
                SearchScope.Project,
                Limit: MaxRankedK,
                MinRelativeScore: 0.0,
                RrfK: point.K,
                FtsWeight: point.FtsWeight,
                VectorWeight: point.VectorWeight), cancellationToken).ConfigureAwait(false)).Results;
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
        TestData.DeleteTempRoot(_dataRoot);

        return ValueTask.CompletedTask;
    }
}

/// <summary>Builds the managed harness once per test collection; the corpus load is the expensive step.</summary>
public sealed class ManagedHarnessFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _sweepGate = new(1, 1);
    private IReadOnlyList<SweepOutcome>? _sweepOutcomes;

    public ManagedHarness Harness { get; private set; } = null!;

    /// <summary>
    ///     Query latencies recorded by <see cref="EnsureSweptAsync"/>'s own sweep only — a slice of
    ///     <see cref="ManagedHarness.QueryLatenciesMs"/> taken around that call, so it excludes samples
    ///     any other fact's own RankAsync calls (e.g. DegenerateQuerySubset, the per-query audit) add
    ///     to that shared list before or after the sweep runs.
    /// </summary>
    public IReadOnlyList<double> SweepQueryLatenciesMs { get; private set; } = [];

    public async ValueTask InitializeAsync() => Harness = await ManagedHarness.BuildAsync();

    public async ValueTask DisposeAsync() => await Harness.DisposeAsync();

    /// <summary>
    ///     Runs the full query sweep once per fixture lifetime (memoized): whichever fact in the
    ///     collection calls this first pays the one corpus pass, every later caller reuses its result
    ///     and <see cref="SweepQueryLatenciesMs"/> — order-independent, no <c>ITestCaseOrderer</c> needed.
    /// </summary>
    public async Task<IReadOnlyList<SweepOutcome>> EnsureSweptAsync(CancellationToken cancellationToken)
    {
        if (_sweepOutcomes is not null)
        {
            return _sweepOutcomes;
        }

        await _sweepGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sweepOutcomes is not null)
            {
                return _sweepOutcomes;
            }

            var before = Harness.QueryLatenciesMs.Count;
            var outcomes = await SweepRunner.RunAsync(
                RealWorldQueries.Queries, Harness.RankAsync, cancellationToken).ConfigureAwait(false);
            SweepQueryLatenciesMs = [.. Harness.QueryLatenciesMs.Skip(before)];
            _sweepOutcomes = outcomes;
            return _sweepOutcomes;
        }
        finally
        {
            _sweepGate.Release();
        }
    }
}

[CollectionDefinition("managed-parity")]
public sealed class ManagedParityCollection : ICollectionFixture<ManagedHarnessFixture>
{
}
