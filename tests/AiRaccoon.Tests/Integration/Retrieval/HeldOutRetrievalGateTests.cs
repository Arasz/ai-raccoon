using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     The retrieval gate that can fail (WP11, docs/adr/0056). Scores the queries whose expected
///     document no parameter sweep tuned over, against pinned per-query floors, and proves the
///     floors discriminate by scoring a deliberately reversed ranking through the same path.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class HeldOutRetrievalGateTests : IDisposable
{
    private const string ProjectId = "job-search-ai-assistant";
    private const int RankCutoff = 5;
    private const int SearchLimit = 10;

    /// <summary>Cross-platform envelope for a pinned ranking number (GoldenFile.RankingTolerance).</summary>
    private const double Tolerance = GoldenFile.RankingTolerance;

    /// <summary>
    ///     Per-query held-out nDCG@5 at the shipped defaults, measured 2026-08-15 over the pinned
    ///     query vectors (docs/adr/0050), so the number is the ranking's and not the host CPU's.
    ///     Raise history: none — first pinning. Lowering an entry is a regression being accepted
    ///     and belongs in a commit message, not in a quiet edit.
    /// </summary>
    private static readonly Dictionary<string, double> HeldOutNdcg5Floor = new(StringComparer.Ordinal)
    {
        ["A8"] = 0.131205,
        ["A9"] = 0.553146,
        ["A10"] = 0.169580
    };

    /// <summary>
    ///     The mean of the floors above. This is the discriminating gate: individual queries can be
    ///     so badly ranked that reversing them helps (A8 does), but the mean cannot survive a
    ///     reversal — see <see cref="ReversedRanking_FailsTheHeldOutMeanFloor" />.
    /// </summary>
    private static readonly double HeldOutMeanFloor = HeldOutNdcg5Floor.Values.Average();

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot;
    private readonly Dictionary<string, HashSet<string>> _fileHashes;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;

    public HeldOutRetrievalGateTests(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-held-out-gate");
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db"), dbPath);

        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            PinnedQueryVectors.EmbeddingService());

        (_, _fileHashes) = CorpusHashMap.Build(dbPath,
            BaselineQueryCatalog.Load().Where(q => q.ExpectedSource is not null).Select(q => q.ExpectedSource!));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The gate proper: every held-out query holds its pinned nDCG@5 floor. A ranking change
    ///     that helps the tuned queries and hurts these fails here.
    /// </summary>
    [Fact]
    public async Task HeldOutQueries_HoldTheirPinnedNdcg5Floor()
    {
        var heldOut = RetrievalTuningSets.HeldOut(BaselineQueryCatalog.Load());
        var below = new List<string>();

        foreach (var query in heldOut)
        {
            var ndcg = await ScoreAsync(query, reverse: false, TestContext.Current.CancellationToken);
            var floor = HeldOutNdcg5Floor[query.Id];
            _output.WriteLine($"{query.Id}: nDCG@5={ndcg:F6} floor={floor:F6} ({query.ExpectedSource})");
            if (ndcg < floor - Tolerance)
            {
                below.Add($"{query.Id} {ndcg:F6} < {floor:F6}");
            }
        }

        below.ShouldBeEmpty(
            $"held-out nDCG@5 regressed below its pinned floor: {string.Join("; ", below)}");
    }

    /// <summary>
    ///     The gate that discriminates. Reversing the fused order — the perturbation the plan names —
    ///     must drive the held-out mean below its floor. The retired assertion (nDCG in [0, 1])
    ///     survives the same reversal on every query, which is why it was never a gate.
    /// </summary>
    [Fact]
    public async Task ReversedRanking_FailsTheHeldOutMeanFloor()
    {
        var heldOut = RetrievalTuningSets.HeldOut(BaselineQueryCatalog.Load());
        var reversed = new List<double>(heldOut.Count);

        foreach (var query in heldOut)
        {
            var score = await ScoreAsync(query, reverse: true, TestContext.Current.CancellationToken);
            score.ShouldBeInRange(0.0, 1.0, $"{query.Id}: the retired range assertion passes on a reversed ranking");
            _output.WriteLine($"{query.Id}: reversed nDCG@5={score:F6} vs floor {HeldOutNdcg5Floor[query.Id]:F6}");
            reversed.Add(score);
        }

        reversed.Average().ShouldBeLessThan(HeldOutMeanFloor - Tolerance,
            $"a reversed ranking must fail the held-out mean floor of {HeldOutMeanFloor:F6}; " +
            "a floor a reversal survives is not a gate");
    }

    /// <summary>
    ///     The held-out mean itself, gated. Per-query floors catch a single regression; this catches
    ///     a change that trades three queries against each other.
    /// </summary>
    [Fact]
    public async Task HeldOutMean_HoldsItsFloor()
    {
        var heldOut = RetrievalTuningSets.HeldOut(BaselineQueryCatalog.Load());
        var scores = new List<double>(heldOut.Count);

        foreach (var query in heldOut)
        {
            scores.Add(await ScoreAsync(query, reverse: false, TestContext.Current.CancellationToken));
        }

        _output.WriteLine($"held-out mean nDCG@5={scores.Average():F6} floor={HeldOutMeanFloor:F6}");
        scores.Average().ShouldBeGreaterThanOrEqualTo(HeldOutMeanFloor - Tolerance);
    }

    /// <summary>
    ///     The measurement WP11 exists to produce: the same search path, the same corpus and the
    ///     same metric over the queries the sweeps tuned on and the queries they never saw. Every
    ///     published retrieval figure comes from the first set.
    /// </summary>
    [Fact]
    public async Task InSampleScore_ExceedsHeldOutScore_OnTheSamePath()
    {
        var catalog = BaselineQueryCatalog.Load();
        var tuning = RetrievalTuningSets.Gradeable(catalog)
            .Where(q => RetrievalTuningSets.TuningQueryIds.Contains(q.Id)).ToList();

        var inSample = new List<double>(tuning.Count);
        foreach (var query in tuning)
        {
            inSample.Add(await ScoreAsync(query, reverse: false, TestContext.Current.CancellationToken));
        }

        var heldOut = new List<double>();
        foreach (var query in RetrievalTuningSets.HeldOut(catalog))
        {
            heldOut.Add(await ScoreAsync(query, reverse: false, TestContext.Current.CancellationToken));
        }

        _output.WriteLine(
            $"in-sample mean nDCG@5={inSample.Average():F6} ({inSample.Count} queries), " +
            $"held-out mean={heldOut.Average():F6} ({heldOut.Count} queries)");
        inSample.Average().ShouldBeGreaterThan(heldOut.Average(),
            "if this ever inverts, the in-sample numbers stopped being optimistic and the tuning " +
            "set stopped being special — re-derive the partition before trusting either");
    }

    private async Task<double> ScoreAsync(CatalogQuery query, bool reverse, CancellationToken cancellationToken)
    {
        var results = (await _store.SearchAsync(new SearchQuery(
            ProjectId, query.Query, SearchScope.Project,
            Limit: SearchLimit, MinRelativeScore: 0.0), cancellationToken)).Results;
        var ranked = results.Select(result => result.Hash).ToList();
        if (reverse)
        {
            ranked.Reverse();
        }

        var relevant = _fileHashes.TryGetValue(CorpusHashMap.FileKey(query.ExpectedSource!), out var hashes)
            ? hashes
            : throw new InvalidOperationException($"{query.Id}: no corpus hashes for '{query.ExpectedSource}'");
        return RetrievalMetrics.NdcgAtK([.. ranked.Take(RankCutoff)], relevant, RankCutoff);
    }
}
