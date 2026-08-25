using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
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
    private const string ProjectId = "ai-raccoon";
    private const int RankCutoff = 5;
    private const int SearchLimit = 10;

    /// <summary>Cross-platform envelope for a pinned ranking number (GoldenFile.RankingTolerance).</summary>
    private const double Tolerance = GoldenFile.RankingTolerance;

    /// <summary>
    ///     Per-query held-out nDCG@5 at the shipped defaults, measured 2026-08-22 on the public
    ///     docs corpus (ADR-0090) over the pinned query vectors (ADR-0050), so the number is the
    ///     ranking's and not the host CPU's. Covers ALL 19 gradeable queries, not three: nothing
    ///     was ever tuned on this corpus, so TuningQueryIds is empty and the held-out set is the
    ///     whole gradeable catalog. Lowering an entry is a regression being accepted and belongs
    ///     in a commit message, not in a quiet edit.
    /// </summary>
    private static readonly Dictionary<string, double> HeldOutNdcg5Floor = new(StringComparer.Ordinal)
    {
        ["A1"] = 0.315648, ["A2"] = 0.339160, ["A3"] = 0.277273, ["A4"] = 0.868795,
        ["A5"] = 1.000000, ["A6"] = 0.722727, ["A7"] = 1.000000, ["A8"] = 0.315648,
        ["A9"] = 0.722727, ["A10"] = 0.508740,
        ["C1"] = 0.500000, ["C2"] = 0.500000, ["C5"] = 0.500000,
        ["S1"] = 1.000000, ["S2"] = 0.169580, ["S3"] = 0.636682, ["S4"] = 0.868795,
        ["S5"] = 1.000000, ["S6"] = 0.684352
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
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "docs-memory.db"), dbPath);

        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            PinnedQueryVectors.EmbeddingService(), null, null, null, null, null, null, null);

        (_, _fileHashes) = CorpusHashMap.Build(dbPath,
            BaselineQueryCatalog.Load().Where(q => q.ExpectedSource is not null).Select(q => q.ExpectedSource!));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The gate proper: every held-out query holds its pinned nDCG@5 floor. A ranking change
    ///     that helps the tuned queries and hurts these fails here.
    /// </summary>
    [RetryFact]
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
    [RetryFact]
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
    [RetryFact]
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

    /* Retired at ADR-0090 —
       RETIRED at ADR-0090, deliberately, not lost. This compared in-sample against held-out
       scores to reproduce ADR-0056's circularity finding. On the public docs corpus there is
       no in-sample population to compare against: nothing was ever tuned here, so
       TuningQueryIds is empty and every gradeable query is out-of-sample. The assertion would
       compare the held-out set against an empty set — vacuous, and it in fact inverted when
       first run here (in-sample mean 0.563017 over 11 ids vs held-out 0.675432 over 6), which
       is exactly the "if this ever inverts, re-derive the partition" case its own message
       named. ADR-0056's measurement is preserved as history in that ADR's status line.
       RetrievalTuningSetsTests.TuningQueryIds_StayEmpty is what keeps this honest: it goes
       red the day someone re-tunes on this corpus, which is when this test becomes
       meaningful again.
    */
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
