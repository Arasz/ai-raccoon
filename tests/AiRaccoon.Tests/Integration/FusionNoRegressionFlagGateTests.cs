using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Integration.Retrieval;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Measures ADR-0078's <see cref="FusionConfigKeys.NoRegressionEnabledGlobal" /> flag against
///     RrfParameterSweepTests' gate (c) population at the production config: does enabling it get
///     A9 (fts rank 2, vector miss, hybrid rank 3) down to rank &lt;= 2, and does it disturb any
///     query the gate already holds without it? Both measured, not assumed — see
///     docs/work/2026-09-24-fusion-no-regression-flag-measured.md for the trace. Answer: A9 stays
///     at rank 3 (the reorder is a structural no-op for a rank-2 single-leg winner when both legs'
///     own rank-1 winners are different chunks — ADR-0078's "position &lt;= L" bound already
///     accounts for this), and three queries the gate holds without the flag (S4, S5, S6) regress
///     into new violations, because the reorder's output is re-merged through source-affinity
///     ranking a second time and that pass can move a result the reorder never touched.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class FusionNoRegressionFlagGateTests : IDisposable
{
    private const string ProjectId = "ai-raccoon";
    private const int SearchLimit = 10;
    private const int ChosenK = 60;
    private const int ChosenFtsWeight = 1;
    private const int ChosenVectorWeight = 1;
    private const double FixedSourceLambda = 0.1;
    private const double FixedConsolidationThreshold = 0.1;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    /// <summary>RrfParameterSweepTests' gate (c) exclusion set, minus A9: this test puts A9 back
    /// under the general rule to measure the flag's effect on it instead of excluding it.</summary>
    private static readonly string[] StillExcluded = ["A2", "A3", "A8", "A10", "C1", "C2", "C5", "S2"];

    /// <summary>Measured 2026-09-24 at the chosen config (k=60, 1:1, lambda=0.1, threshold=0.1,
    /// Max) with the flag on: pinned so a change to this measurement — better or worse — is
    /// visible rather than silently absorbed into "no violations". A9's own pinned ceiling in
    /// RrfParameterSweepTests is 3; this shows the flag does not lower it further, and does not
    /// raise it either.</summary>
    private static readonly IReadOnlyDictionary<string, int> MeasuredHybridRankWithFlagOn =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["A9"] = 3, ["S4"] = 3, ["S5"] = 7, ["S6"] = 3 };

    private static readonly string[] RrfGateQueryIds =
        RetrievalTuningSets.SweepGateQueryIds(BaselineQueryCatalog.Load());

    private readonly string _dataRoot;
    private readonly Dictionary<string, string> _hashMap;
    private readonly ITestOutputHelper _output;
    private readonly SqliteMemoryStore _store;

    public FusionNoRegressionFlagGateTests(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-fusion-flag-gate");
        var bundledDb = Path.Combine(AppContext.BaseDirectory, "Resources", "docs-memory.db");
        TestData.CopyMiniLmCorpusBank(bundledDb, Path.Combine(_dataRoot, "memory.db"));

        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            PinnedQueryVectors.EmbeddingService(), null, null, null, null, null, null, null);

        (_hashMap, _) = CorpusHashMap.Build(
            Path.Combine(_dataRoot, "memory.db"),
            BaselineQueryCatalog.Load()
                .Where(q => q.ExpectedSource is not null && RrfGateQueryIds.Contains(q.Id))
                .Select(q => q.ExpectedSource!));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     With the flag on, every gate (c) query outside the measured/pinned set above must still
    ///     hold hybrid &lt;= best single leg, and the pinned set must hold its measured rank exactly
    ///     — a drift either way (the flag starts helping A9, or starts hurting a query it doesn't
    ///     touch today) is a finding for docs/work/2026-09-24-fusion-no-regression-flag-measured.md,
    ///     not a silent pass.
    /// </summary>
    [RetryFact]
    public async Task Sweep_FlagEnabled_HoldsExceptTheMeasuredA9S4S5S6Regressions()
    {
        await _store.SetSettingAsync(FusionConfigKeys.NoRegressionEnabledGlobal, "true",
            TestContext.Current.CancellationToken);

        var queries = BaselineQueryCatalog.Load()
            .Where(q => q.ExpectedSource is not null && RrfGateQueryIds.Contains(q.Id))
            .ToList();

        var violations = new List<string>();
        var pinnedRanks = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            var hybridRank = await ExactRankAsync(query, ChosenFtsWeight, ChosenVectorWeight,
                TestContext.Current.CancellationToken);
            var ftsRank = await ExactRankAsync(query, 1, 0, TestContext.Current.CancellationToken);
            var vectorRank = await ExactRankAsync(query, 0, 1, TestContext.Current.CancellationToken);
            var bestSingle = Min(ftsRank, vectorRank);

            _output.WriteLine(
                $"{query.Id}: hybrid={hybridRank?.ToString() ?? "-"} fts={ftsRank?.ToString() ?? "-"} " +
                $"vector={vectorRank?.ToString() ?? "-"} bestSingle={bestSingle?.ToString() ?? "-"}");

            if (MeasuredHybridRankWithFlagOn.ContainsKey(query.Id))
            {
                pinnedRanks[query.Id] = hybridRank;
                continue;
            }

            if (StillExcluded.Contains(query.Id) || bestSingle is null)
            {
                continue;
            }

            if (hybridRank is null || hybridRank.Value > bestSingle.Value)
            {
                violations.Add(
                    $"{query.Id}: hybrid {hybridRank?.ToString() ?? "-"} > best single leg {bestSingle} " +
                    $"(fts {ftsRank?.ToString() ?? "-"}, vector {vectorRank?.ToString() ?? "-"})");
            }
        }

        violations.ShouldBeEmpty(
            $"flag-enabled fusion introduced a regression outside the measured/pinned set: {string.Join("; ", violations)}");

        foreach (var (id, measuredRank) in MeasuredHybridRankWithFlagOn)
        {
            pinnedRanks[id].ShouldBe(measuredRank,
                $"{id}: flag-enabled hybrid rank drifted from the 2026-09-24 measurement ({measuredRank}) — " +
                "re-measure and update docs/work/2026-09-24-fusion-no-regression-flag-measured.md");
        }
    }

    private async Task<int?> ExactRankAsync(
        CatalogQuery query, int ftsWeight, int vectorWeight, CancellationToken cancellationToken)
    {
        var expectedHash = _hashMap[query.ExpectedSource!];
        var results = (await _store.SearchAsync(new SearchQuery(
            ProjectId, query.Query, SearchScope.Project,
            Limit: SearchLimit, MinRelativeScore: 0.0, RrfK: ChosenK,
            FtsWeight: ftsWeight, VectorWeight: vectorWeight,
            SourceLambda: FixedSourceLambda, ConsolidationThreshold: FixedConsolidationThreshold,
            DocScoreFormula: DocScoreFormula.Max, CandidateWindow: CandidateWindowMode.Max3X100),
            cancellationToken)).Results;

        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Hash == expectedHash)
            {
                return i + 1;
            }
        }

        return null;
    }

    private static int? Min(int? first, int? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return Math.Min(first.Value, second.Value);
    }
}
