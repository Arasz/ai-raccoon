using System.Text.Json;
using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Storage;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     P7 (Stage 1, plan §§2–5, normative §9 M7): integration + end-to-end proof. The P1–P6b
///     evidence flow (fuse → sidecar → pipeline → envelope → MCP) and the telemetry join
///     (tolerant-ensured result_features + three metric series) are exercised live against
///     seeded banks. Tests + wiring only: no new production type exists in this package —
///     the two named hookups (SearchDispatchResult → MemoryTools threading, telemetry
///     call-site) landed in P5/P6a/P6b and are verified here, never extended.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchSignalPreservationStageOneTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    // The shipped fusion shape (plan §3): k=60, 1:1 weights. Passed explicitly on every
    // evidence-flow search so the hand computation below owns its constants instead of
    // inheriting whatever the defaults drift to.
    private const int RrfK = 60;
    private const int UnitWeight = 1;

    // Pinned by running LiveSearch_WithVectorLegFiring_IssuesPinnedStatementCount: the count the
    // both-legs search path issues today, with evidence flowing on both legs (5 open PRAGMAs,
    // 3 schema/watch checks, the 2-statement settings snapshot, 5 embedding-setting reads, 1 context
    // resolve, 2 shared + 2 project vector-candidate queries, 2 FTS candidate queries, 1 grouped
    // snippet lookup, and 1 access bump per served row (5)). Deliberate, not incidental —
    // pair-update with the two FTS-only pins (SearchEvidencePipelineTests and the
    // P7 G5 conjunction below): any search-path query change must reconcile all three.
    private const int ExpectedVectorStatementCount = 28;

    private readonly List<string> _roots = [];
    private FakeEmbeddingEndpoint _openAi = null!;

    public async ValueTask InitializeAsync()
    {
        _openAi = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _openAi.DisposeAsync();
        foreach (var root in _roots)
        {
            TestData.DeleteTempRoot(root);
        }
    }

    /// <summary>
    ///     (1) End-to-end evidence flow: a seeded bank, leg ranks observed from single-leg
    ///     runs, then a live both-leg memory_search whose strengths/legs/margins must equal
    ///     the §3 hand computation (test-owned arithmetic over the observed ranks — never
    ///     FusionEvidence.FromRaws, per S4). sourceLambda 0 keeps the merger a monotone
    ///     re-normalization so single-leg served order IS leg-rank order.
    /// </summary>
    [RetryFact]
    public async Task LiveSearch_BothLegs_ReturnsExactHandComputedStrengthsLegsAndMargins()
    {
        var bank = await CreateHarborBankAsync(TestContext.Current.CancellationToken);
        var tools = BuildTools(bank.Store);
        var ct = TestContext.Current.CancellationToken;

        var ftsOnly = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            rrfK: RrfK, ftsWeight: UnitWeight, vectorWeight: 0, sourceLambda: 0.0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var vectorOnly = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            rrfK: RrfK, ftsWeight: 0, vectorWeight: UnitWeight, sourceLambda: 0.0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var both = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            rrfK: RrfK, ftsWeight: UnitWeight, vectorWeight: UnitWeight, sourceLambda: 0.0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");

        var ftsHashes = ftsOnly.Data!.Results.Select(r => r.Hash).ToList();
        var vectorHashes = vectorOnly.Data!.Results.Select(r => r.Hash).ToList();
        ftsHashes.ShouldNotBeEmpty("the two-leg premise needs a firing FTS leg");
        vectorHashes.ShouldNotBeEmpty("the two-leg premise needs a firing vector leg");
        ftsHashes.Count.ShouldBe(5, "every seed shares the query term, so FTS must return all five");
        vectorHashes.Count.ShouldBe(5, "the candidate window covers all five rows, so the vector leg returns all five");

        // 1-based positions in the single-leg served orders are the leg ranks.
        var ftsRank = RankByPosition(ftsHashes);
        var vectorRank = RankByPosition(vectorHashes);
        var union = ftsHashes.Union(vectorHashes, StringComparer.Ordinal).ToList();

        var maxPossible = UnitWeight / (double)(RrfK + 1) + UnitWeight / (double)(RrfK + 1);
        var expectedRaw = union.ToDictionary(
            hash => hash,
            hash => (ftsRank.TryGetValue(hash, out var fts) ? UnitWeight / (double)(RrfK + fts) : 0.0)
                + (vectorRank.TryGetValue(hash, out var vec) ? UnitWeight / (double)(RrfK + vec) : 0.0),
            StringComparer.Ordinal);
        // Documented FromRaws order: raw descending, content-hash ordinal on ties.
        var expectedOrder = union
            .OrderByDescending(hash => expectedRaw[hash])
            .ThenBy(hash => hash, StringComparer.Ordinal)
            .ToList();
        var expectedTopMargin = (expectedRaw[expectedOrder[0]] - expectedRaw[expectedOrder[1]]) / expectedRaw[expectedOrder[0]];
        var middle = expectedOrder.Count / 2;
        var expectedMedian = expectedOrder.Count % 2 == 1
            ? expectedRaw[expectedOrder[middle]]
            : (expectedRaw[expectedOrder[middle - 1]] + expectedRaw[expectedOrder[middle]]) / 2.0;
        var expectedTopVsMedian = (expectedRaw[expectedOrder[0]] - expectedMedian) / expectedRaw[expectedOrder[0]];

        var served = both.Data!.Results.Select(r => r.Hash).ToList();
        served.ShouldBe(expectedOrder, ignoreOrder: true,
            "floor 0 + limit 50 keeps the whole fused population: the served set is the leg union");
        var evidence = both.Data!.EvidenceByHash.ShouldNotBeNull();
        evidence.Keys.ShouldBe(served, ignoreOrder: true,
            "the S3 join covers exactly the served rows — served-rows-only evidence");

        foreach (var hash in served)
        {
            var item = evidence[hash];
            item.Hash.ShouldBe(hash);
            var expectedLegs = new List<LegRank>();
            if (ftsRank.TryGetValue(hash, out var fts))
            {
                expectedLegs.Add(new LegRank("fts", fts));
            }

            if (vectorRank.TryGetValue(hash, out var vec))
            {
                expectedLegs.Add(new LegRank("vector", vec));
            }

            item.Legs.ShouldBe(expectedLegs,
                "each hash names exactly the legs that returned it, at the observed ranks, fts-first");
            item.FusionStrength.ShouldBe(expectedRaw[hash] / maxPossible, 1e-9,
                "strength is raw/maxPossible over the observed ranks — the §3 formula, live");
            if (vectorRank.ContainsKey(hash))
            {
                item.Cosine.ShouldNotBeNull("a vector-participating hash carries its fused cosine");
                double.IsFinite(item.Cosine.Value).ShouldBeTrue("only finite cosines ever reach the wire (P3 rule)");
            }
            else
            {
                item.Cosine.ShouldBeNull("no vector leg means no cosine, never a 0.0");
            }
        }

        var stats = both.Data!.FusionStats.ShouldNotBeNull();
        stats.MaxPossible.ShouldBe(maxPossible, 1e-12);
        stats.ParticipatingLegs.ShouldBe(["fts", "vector"]);
        stats.TopMargin.ShouldNotBeNull();
        stats.TopMargin.Value.ShouldBe(expectedTopMargin, 1e-9);
        stats.TopVsMedian.ShouldNotBeNull();
        stats.TopVsMedian.Value.ShouldBe(expectedTopVsMedian, 1e-9);
    }

    /// <summary>
    ///     (2) Telemetry join: one live search accrues metrics rows + the result_features
    ///     column under its correlation id; a subsequent grade AND follow-through land on
    ///     the same row — one labeled Stage-2 row with zero later joinery (plan §5).
    /// </summary>
    [RetryFact]
    public async Task LiveSearch_JoinsMetricsQualityGradeAndFollowThrough_IntoOneLabeledRow()
    {
        var bank = await CreateHarborBankAsync(TestContext.Current.CancellationToken);
        var buffer = new MeasurementBuffer(1000);
        var quality = new SqliteSearchQualityService(bank.Factory, NullLogger<SqliteSearchQualityService>.Instance);
        var tools = BuildTools(bank.Store, quality,
            new MetricsRecorder(buffer, NullLogger<MetricsRecorder>.Instance));
        var ct = TestContext.Current.CancellationToken;

        var envelope = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            sourceLambda: 0.0, kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var correlationId = envelope.Meta.CorrelationId.ShouldNotBeNull();
        var served = envelope.Data!.Results.Select(r => r.Hash).ToList();
        served.ShouldNotBeEmpty("the join proof needs served rows to carry features for");

        var flusher = new MetricsFlusher(buffer,
            new SqliteMetricsStore(bank.Factory, NullLogger<SqliteMetricsStore>.Instance),
            new InMemorySettings(), bank.Clock, TestTelemetry.None, NullLogger<MetricsFlusher>.Instance);
        await flusher.FlushOnceAsync(ct);

        await using var connection = await bank.Factory.OpenBankAsync(ct);
        var metrics = (await connection.QueryAsync<MetricRow>(
            new CommandDefinition(
                "SELECT name AS Name, query_hash AS QueryHash, correlation_id AS CorrelationId, tags AS Tags " +
                "FROM metrics WHERE correlation_id = @Id",
                new { Id = correlationId }, cancellationToken: ct))).ToList();
        var expectedNames = SearchTimings.SeriesNames
            .Concat(FusionStats.MetricNames)
            .ToList();
        metrics.Select(m => m.Name).ShouldBe(expectedNames, ignoreOrder: true,
            "one search accrues the ten phase series plus the three Stage-1 shape series");
        metrics.ShouldAllBe(m => m.QueryHash == ContentHash.OfValue("harbor"),
            "kind=memory keeps its query hash on every series");
        metrics.ShouldAllBe(m => m.Tags == null, "no query text anywhere in the row");

        var row = await ReadQualityRowAsync(connection, correlationId, ct);
        row.ResultCount.ShouldBe((long)served.Count);
        var featureHashes = FeatureHashes(row.ResultFeatures.ShouldNotBeNull());
        featureHashes.ShouldBe(served,
            "result_features covers the served rows in served order — the dispatcher join, persisted");

        var followFile = envelope.Data!.Results[0].SourceFile.ShouldNotBeNull();
        await quality.RecordGradeAsync("acme", correlationId, 5, "p7 telemetry join", ct);
        await quality.RecordFollowThroughAsync(correlationId, followFile, ct: ct);

        var labeled = await ReadQualityRowAsync(connection, correlationId, ct);
        labeled.UsefulnessGrade.ShouldBe(5L);
        labeled.FollowThroughCount.ShouldBe(1L);
        labeled.FollowThroughFiles.ShouldNotBeNull().ShouldContain(followFile);
        FeatureHashes(labeled.ResultFeatures.ShouldNotBeNull()).ShouldBe(served,
            "labels land on the feature row itself: one correlation id holds features, grade, and follow-through");
    }

    /// <summary>
    ///     (3) Baseline-order parity on a multi-context bank: determinism across calls,
    ///     floor/limit subset semantics against the floor-0 run, the ADR-0035 shared/project
    ///     duplicate collapsing pre-Fuse with the project copy winning, and the kind=both
    ///     memory section byte-equal to kind=memory. Runs on production defaults (no
    ///     sourceLambda override, so lambda 0.1 is in effect) with a floor sweep, limit
    ///     truncation, and a ShareAsync-promoted shared/project duplicate pair (S2 — the
    ///     ADR-0035 dedup runs pre-Fuse).
    /// </summary>
    [RetryFact]
    public async Task MultiContextBank_PreservesBaselineOrderFloorLimitSemanticsAndDedup()
    {
        var bank = await CreateHarborBankAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        var entries = bank.Entries;
        var shared = await bank.Store.ShareAsync("acme", entries[0].Hash, ct);
        shared.Entry.Hash.ShouldNotBe(entries[0].Hash,
            "a promoted shared copy carries a different row hash — otherwise this is not a real dedup test");
        var tools = BuildTools(bank.Store);

        var wide = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var wideAgain = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var wideKeys = ServedKeys(wide);
        ServedKeys(wideAgain).ShouldBe(wideKeys, "the same query against the same bank serves the same sequence");

        wideKeys.Count(h => h == entries[0].Hash).ShouldBe(1,
            "ADR-0035: the promoted shared copy and its project original must not double-count");
        wideKeys.ShouldContain(entries[0].Hash, "the project-scoped copy wins its content group");

        var floored = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.6,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var wideByHash = wide.Data!.Results.ToDictionary(r => r.Hash, r => r.Ranking, StringComparer.Ordinal);
        ServedKeys(floored).ShouldBe(wideKeys.Where(h => wideByHash[h] >= 0.6).ToList(),
            "the relative floor filters the same normalized population — evidence changes no floor semantics");
        var wideEvidence = wide.Data!.EvidenceByHash.ShouldNotBeNull();
        if (floored.Data!.Results.Count > 0)
        {
            var flooredEvidence = floored.Data!.EvidenceByHash.ShouldNotBeNull();
            foreach (var hash in ServedKeys(floored))
            {
                EvidenceShouldMatch(flooredEvidence[hash], wideEvidence[hash]);
            }
        }

        var limited = await tools.Search("acme", "harbor", scope: "all", limit: 2, minRelativeScore: 0.0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        ServedKeys(limited).ShouldBe(wideKeys.Take(2).ToList(), "limit truncates the same order — evidence moves no row");

        var both = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            kind: "both", cancellationToken: ct, sessionId: "sess-test");
        ServedKeys(both).ShouldBe(wideKeys, "the code leg never perturbs memory ordering");
        ServedRankings(both).ShouldBe(ServedRankings(wide), "memory rankings identical across kinds");
        var bothEvidence = both.Data!.EvidenceByHash.ShouldNotBeNull();
        bothEvidence.Keys.ShouldBe(wideEvidence.Keys, ignoreOrder: true);
        foreach (var hash in bothEvidence.Keys)
        {
            EvidenceShouldMatch(bothEvidence[hash], wideEvidence[hash]);
        }
    }

    /// <summary>
    ///     (4) End-to-end G5 (M6 conjunction, P7-owned): capture + metrics + quality column
    ///     jointly add zero statements beyond the pinned singles. Differential by design:
    ///     the equipped run (real quality service + buffered recorder) replays every
    ///     plain-path statement and adds only the quality path's own bank open (byte-
    ///     identical ensure queries, pre-S1 behavior) plus its single INSERT — with zero
    ///     metrics-table writes on the caller's thread and identical served rows.
    /// </summary>
    [RetryFact]
    public async Task EquippedSearch_AddsExactlyOneStatementBeyondTheUnequippedPath()
    {
        var dataRoot = TestData.CreateTempRoot("airaccoon-p7-g5-conjunction");
        _roots.Add(dataRoot);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        var inner = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var traced = new List<string>();
        var factory = new CountingFactory(inner, sql =>
        {
            // P4's application-statement gate: SQLite prefixes FTS5-internal shadow-table
            // traffic with "--"; the S1-added-query tripwire watches Dapper-level statements.
            if (!sql.StartsWith("--", StringComparison.Ordinal))
            {
                traced.Add(sql);
            }
        });
        var store = SearchTimingsHarness.CreateStore(factory, clock, new SearchTimingsHarness.VectorEmbedderStub());
        var ct = TestContext.Current.CancellationToken;
        await store.WriteAsync(new MemoryWriteRequest("proj-1", "widgets are stocked on the shelf"), ct);
        await store.WriteAsync(new MemoryWriteRequest("proj-1", "the warehouse tracks widgets every day"), ct);
        await store.WriteAsync(new MemoryWriteRequest("proj-1", "spare widgets line the back room wall"), ct);

        var unequipped = BuildTools(store);
        var buffer = new MeasurementBuffer(1000);
        var quality = new SqliteSearchQualityService(factory, NullLogger<SqliteSearchQualityService>.Instance);
        var equipped = BuildTools(store, quality,
            new MetricsRecorder(buffer, NullLogger<MetricsRecorder>.Instance));

        // Warm-up settles first-touch writes (access bumps) outside the traced window.
        await unequipped.Search("proj-1", "widgets", scope: "project", vectorWeight: 0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");

        traced.Clear();
        var plain = await unequipped.Search("proj-1", "widgets", scope: "project", vectorWeight: 0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var plainStatements = traced.ToList();

        traced.Clear();
        var wired = await equipped.Search("proj-1", "widgets", scope: "project", vectorWeight: 0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var wiredStatements = traced.ToList();
        var wiredCorrelationId = wired.Meta.CorrelationId.ShouldNotBeNull();

        plain.Data!.EvidenceByHash.ShouldNotBeNull("the pin is meaningless unless evidence flowed on the plain path too");
        wired.Data!.EvidenceByHash.ShouldNotBeNull("the pin is meaningless unless evidence flowed on the wired path");
        plainStatements.Count.ShouldBe(16,
            "the P4 FTS-only pin re-proven through the full tools path (vectorWeight: 0 — the " +
            "both-legs path has its own pin, LiveSearch_WithVectorLegFiring_IssuesPinnedStatementCount): " +
            "gate, guard, and buffered recording add zero SQL to the search itself. " +
            "Pair-update with SearchEvidencePipelineTests.ExpectedStatementCount.");
        ServedKeys(wired).ShouldBe(ServedKeys(plain), "telemetry changes no served row");
        ServedRankings(wired).ShouldBe(ServedRankings(plain), "telemetry changes no ranking");

        // Multiset difference: the equipped run must add nothing beyond the plain run's
        // statements except the quality path's own bank open plus its single INSERT.
        var remaining = new List<string>(plainStatements);
        var extra = new List<string>();
        foreach (var statement in wiredStatements)
        {
            var match = remaining.IndexOf(statement);
            if (match >= 0)
            {
                remaining.RemoveAt(match);
            }
            else
            {
                extra.Add(statement);
            }
        }

        remaining.ShouldBeEmpty("every plain-path statement still runs on the equipped path");
        var inserts = extra.Where(s => s.Contains("search_quality", StringComparison.Ordinal)).ToList();
        inserts.Count.ShouldBe(1, "M6: the quality column rides one INSERT on the already-written row");
        inserts[0].ShouldContain("INSERT INTO search_quality");
        var ensure = extra.Except(inserts).ToList();
        ensure.ShouldAllBe(s => plainStatements.Contains(s, StringComparer.Ordinal),
            "everything else the equipped run adds is the quality service's own bank open " +
            "(PRAGMAs + watch/trigger checks, byte-identical to the search connection's own " +
            "open sequence) — pre-S1 OpenBankAsync behavior, not S1 telemetry");
        extra.ShouldNotContain(s => s.Contains("metrics", StringComparison.OrdinalIgnoreCase),
            "G5: no measurement reaches its table on the caller's thread — the flusher owns that write");
        buffer.EnqueuedCount.ShouldBe((long)SearchTimings.SeriesNames.Count + 3,
            "ten phase series plus top_strength/top_margin/legs_fired all enqueue without issuing SQL");

        await using var connection = await inner.OpenBankAsync(ct);
        var features = await connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition("SELECT result_features FROM search_quality WHERE correlation_id = @Id",
                new { Id = wiredCorrelationId }, cancellationToken: ct));
        FeatureHashes(features.ShouldNotBeNull()).ShouldBe(ServedKeys(wired),
            "the single INSERT carried the served-subset features with it");
    }

    /// <summary>
    ///     F3 (G5 vector path): the statement-count tripwire with the vector leg genuinely firing —
    ///     seeded harbor bank plus FakeEmbeddingEndpoint vectors, so ParticipatingLegs must name
    ///     both legs and vector rows must carry cosines, or the pin is meaningless. The FTS-only
    ///     pins (P4 SearchEvidencePipelineTests + the G5 conjunction below, both vectorWeight: 0)
    ///     cannot see a query smuggled onto the embedding/vector path; this pin breaks on exactly
    ///     that. Pair-update with those two FTS-only pins: any search-path query change must
    ///     reconcile all three.
    /// </summary>
    [RetryFact]
    public async Task LiveSearch_WithVectorLegFiring_IssuesPinnedStatementCount()
    {
        var traced = new List<string>();
        var bank = await CreateHarborBankAsync(TestContext.Current.CancellationToken, sql => traced.Add(sql));
        var tools = BuildTools(bank.Store);
        var ct = TestContext.Current.CancellationToken;

        traced.Clear();
        var both = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            rrfK: RrfK, ftsWeight: UnitWeight, vectorWeight: UnitWeight, sourceLambda: 0.0,
            kind: "memory", cancellationToken: ct, sessionId: "sess-test");

        var stats = both.Data!.FusionStats.ShouldNotBeNull();
        stats.ParticipatingLegs.ShouldBe(["fts", "vector"],
            "the pin is meaningless unless the vector leg genuinely fired");
        var evidence = both.Data!.EvidenceByHash.ShouldNotBeNull();
        evidence.Values.Where(row => row.Cosine is not null).ShouldNotBeEmpty(
            "vector-participating rows carry their fused cosine");
        traced.Count.ShouldBe(ExpectedVectorStatementCount,
            "G5 on the both-legs path: capture adds zero SQL beyond the pinned search shape");
    }

    /// <summary>
    ///     (5) Stats-invariance under limit/floor changes (S5): FusionStats describes the
    ///     PRE-floor candidate population, so narrowing the served set must not move it —
    ///     while the served set itself narrows exactly as before.
    /// </summary>
    [RetryFact]
    public async Task NarrowingLimitOrFloor_LeavesFusionStatsUnmoved_WhileNarrowingServedRows()
    {
        var bank = await CreateHarborBankAsync(TestContext.Current.CancellationToken);
        var tools = BuildTools(bank.Store);
        var ct = TestContext.Current.CancellationToken;

        var wide = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            sourceLambda: 0.0, kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var floored = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.5,
            sourceLambda: 0.0, kind: "memory", cancellationToken: ct, sessionId: "sess-test");
        var tiny = await tools.Search("acme", "harbor", scope: "all", limit: 1, minRelativeScore: 0.0,
            sourceLambda: 0.0, kind: "memory", cancellationToken: ct, sessionId: "sess-test");

        var wideStats = wide.Data!.FusionStats.ShouldNotBeNull();
        var flooredStats = floored.Data!.FusionStats.ShouldNotBeNull();
        var tinyStats = tiny.Data!.FusionStats.ShouldNotBeNull();
        StatsShouldMatch(flooredStats, wideStats);
        StatsShouldMatch(tinyStats, wideStats);

        var wideKeys = ServedKeys(wide);
        ServedKeys(tiny).ShouldBe(wideKeys.Take(1).ToList(), "limit 1 serves the head of the same order");
        var tinyEvidence = tiny.Data!.EvidenceByHash.ShouldNotBeNull();
        tinyEvidence.Keys.ShouldBe(ServedKeys(tiny));
        EvidenceShouldMatch(tinyEvidence[ServedKeys(tiny)[0]], wide.Data!.EvidenceByHash.ShouldNotBeNull()[ServedKeys(tiny)[0]]);
        var wideByHash = wide.Data!.Results.ToDictionary(r => r.Hash, r => r.Ranking, StringComparer.Ordinal);
        ServedKeys(floored).ShouldBe(wideKeys.Where(h => wideByHash[h] >= 0.5).ToList());
        ServedKeys(floored).ShouldNotBeEmpty("rank 1 always scores 1.0, so floor 0.5 always keeps at least the head");
    }

    /// <summary>
    ///     (6) kind=both on the wire: the code corpus serves beside intact memory evidence —
    ///     code hashes never pick up doc evidence (hash-namespace isolation, §8) and the
    ///     memory section matches the kind=memory run.
    /// </summary>
    [RetryFact]
    public async Task KindBoth_ServesCodeBesideIntactMemoryEvidence_WithNoCrossPickup()
    {
        var bank = await CreateHarborBankAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        await using (var connection = await bank.Factory.OpenBankAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                VALUES (1, 'hash-code-1', 'src/Harbor.cs', 'sealed class HarborRegistry { } // harbor pilot log', 'src/Harbor.cs', 1, 3, 'acme', 1, 1)
                """,
                cancellationToken: ct));
        }

        var codeSearch = new SqliteCodeSearchService(bank.Factory, new FakeCodeEmbedder());
        var tools = BuildTools(bank.Store, codeSearch: codeSearch);

        var both = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            sourceLambda: 0.0, kind: "both", cancellationToken: ct, sessionId: "sess-test");
        var memory = await tools.Search("acme", "harbor", scope: "all", limit: 50, minRelativeScore: 0.0,
            sourceLambda: 0.0, kind: "memory", cancellationToken: ct, sessionId: "sess-test");

        var code = both.Data!.Code.ShouldNotBeNull("the seeded code corpus must serve on kind=both");
        code.Select(c => c.Path).ShouldContain("src/Harbor.cs");
        var memoryKeys = ServedKeys(memory);
        memoryKeys.ShouldNotBeEmpty();
        ServedKeys(both).ShouldBe(memoryKeys, "the memory section is untouched by the code leg");
        var evidence = both.Data!.EvidenceByHash.ShouldNotBeNull("memory evidence stays intact beside code hits");
        evidence.Keys.ShouldBe(memoryKeys, ignoreOrder: true);
        foreach (var codeHit in code)
        {
            evidence.ShouldNotContainKey(codeHit.Hash,
                "code hashes live in a separate namespace — the S3 join iterates memory rows only");
        }
    }

    private async Task<HarborBank> CreateHarborBankAsync(CancellationToken cancellationToken, Action<string>? onStatement = null)
    {
        var dataRoot = TestData.CreateTempRoot("airaccoon-p7-s1");
        _roots.Add(dataRoot);
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        ISqliteConnectionFactory factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        if (onStatement is not null)
        {
            // F3 vector-path pin: the same observed-not-assumed trace mechanism as the P4 pin
            // (copied), so the both-legs search shape is pinned statement-for-statement.
            factory = new CountingFactory(factory, sql =>
            {
                if (!sql.StartsWith("--", StringComparison.Ordinal))
                {
                    onStatement(sql);
                }
            });
        }
        var clock = new FakeTimeProvider(FixedNow);
        var settings = new SqliteSettingsStore(factory);
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), new StubChunker(), clock,
            TestData.CreateEmbeddingService(), modelMigrationLease: null, jsonChunker: null, noisePolicies: null,
            settings: settings, codeChunker: null, ignoreRulesProvider: null, measurements: null);
        await store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", cancellationToken);
        await TestData.ConfigureAndDrainEmbeddingAsync(store, factory, TestData.CreateEmbeddingService(),
            "openai", "nomic-embed-text", _openAi.BaseUrl, cancellationToken, clock);

        // Every seed shares the query term "harbor" (single-token query matches all five in
        // FTS; the vector leg ranks all five by cosine), carries a distinct content so no
        // two rows dedupe, and owns a distinct source file so no two rows are siblings.
        var seeds = new[]
        {
            new Seed("harbor ledger tracks anchovy shipments daily", "notes/ledger.md"),
            new Seed("anchovy shipments arrive at the harbor every morning", "notes/morning.md"),
            new Seed("the harbor lighthouse keeper polishes the brass lamp", "notes/lighthouse.md"),
            new Seed("brass lamp oil fills the harbor storeroom shelves", "notes/storeroom.md"),
            new Seed("the tide charts hang inside the harbor office", "notes/office.md")
        };
        var entries = new List<MemoryEntry>();
        foreach (var seed in seeds)
        {
            entries.Add(await store.WriteAsync(
                new MemoryWriteRequest("acme", seed.Value, SourceFile: seed.SourceFile), cancellationToken));
        }

        return new HarborBank(factory, store, clock, entries);
    }

    private static MemoryTools BuildTools(
        SqliteMemoryStore store,
        ISearchQualityService? quality = null,
        IMeasurementRecorder? recorder = null,
        ICodeSearchService? codeSearch = null)
    {
        var gate = new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue(),
            new NeverMigratingStore(), new AllowingRegistrationGuard());
        return new MemoryTools(store, gate,
            new SearchDispatcher(store, codeSearch ?? new NoOpCodeSearchService(),
                quality ?? new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(store, new FakePromotionQueue()),
            recorder ?? new NoOpMeasurementRecorder(),
            NullLogger<MemoryTools>.Instance);
    }

    private static void EvidenceShouldMatch(RetrievalEvidence actual, RetrievalEvidence expected)
    {
        // Member-wise, not record ShouldBe: record equality compares the Legs lists by
        // reference, so two runs' identical evidence would never be "equal".
        actual.Hash.ShouldBe(expected.Hash);
        actual.FusionStrength.ShouldBe(expected.FusionStrength);
        actual.Legs.ShouldBe(expected.Legs);
        actual.Cosine.ShouldBe(expected.Cosine);
    }

    private static void StatsShouldMatch(FusionStats actual, FusionStats expected)
    {
        // Same reference-equality trap as above, via ParticipatingLegs: compare members.
        // Exact doubles: both sides recompute the same population deterministically. The
        // floor applies after stats are computed and the limit truncates serving only —
        // the same population always yields the same shape.
        actual.TopMargin.ShouldBe(expected.TopMargin);
        actual.TopVsMedian.ShouldBe(expected.TopVsMedian);
        actual.MaxPossible.ShouldBe(expected.MaxPossible);
        actual.ParticipatingLegs.ShouldBe(expected.ParticipatingLegs);
    }

    private static Dictionary<string, int> RankByPosition(IReadOnlyList<string> hashes)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var rank = 1; rank <= hashes.Count; rank++)
        {
            ranks[hashes[rank - 1]] = rank;
        }

        return ranks;
    }

    private static List<string> ServedKeys(ApiEnvelope<MemoryTools.SearchResultList> envelope) =>
        envelope.Data!.Results.Select(r => r.Hash).ToList();

    private static List<double> ServedRankings(ApiEnvelope<MemoryTools.SearchResultList> envelope) =>
        envelope.Data!.Results.Select(r => r.Ranking).ToList();

    private static List<string> FeatureHashes(string resultFeatures)
    {
        using var document = JsonDocument.Parse(resultFeatures);
        return document.RootElement.EnumerateArray()
            .Select(row => row.GetProperty("hash").GetString().ShouldNotBeNull())
            .ToList();
    }

    private static async Task<QualityRow> ReadQualityRowAsync(SqliteConnection connection, string correlationId,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleAsync<QualityRow>(new CommandDefinition(
            "SELECT result_count AS ResultCount, top_source_files AS TopSourceFiles, " +
            "result_features AS ResultFeatures, usefulness_grade AS UsefulnessGrade, " +
            "follow_through_count AS FollowThroughCount, follow_through_files AS FollowThroughFiles " +
            "FROM search_quality WHERE correlation_id = @Id",
            new { Id = correlationId }, cancellationToken: cancellationToken));
    }

    private sealed record HarborBank(ISqliteConnectionFactory Factory, SqliteMemoryStore Store,
        FakeTimeProvider Clock, IReadOnlyList<MemoryEntry> Entries);

    private sealed record Seed(string Value, string SourceFile);

    private sealed record MetricRow(string Name, string? QueryHash, string? CorrelationId, string? Tags);

    private sealed record QualityRow(long ResultCount, string? TopSourceFiles, string? ResultFeatures,
        long? UsefulnessGrade, long? FollowThroughCount, string? FollowThroughFiles);

    /// <summary>
    ///     Counts every SQL statement on connections this factory hands out (P4's pin
    ///     mechanism, copied: the G5 conjunction needs the same observed-not-assumed count
    ///     on the full tools path). One hook per connection; the hook never issues SQL.
    /// </summary>
    private sealed class CountingFactory(ISqliteConnectionFactory inner, Action<string> onStatement)
        : ISqliteConnectionFactory
    {
        private readonly object _gate = new();
        private readonly Dictionary<SqliteConnection, SQLitePCL.strdelegate_trace> _hooks = new(ReferenceEqualityComparer.Instance);

        public string BankPath => inner.BankPath;

        public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenBankAsync(cancellationToken);
            Trace(connection);
            return connection;
        }

        public Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default) =>
            inner.MigrateLegacyKeyAsync(cancellationToken);

        public Task<SqliteConnection> OpenBankWithResolvedKeyAsync(ResolvedKey resolvedKey,
            CancellationToken cancellationToken = default) =>
            inner.OpenBankWithResolvedKeyAsync(resolvedKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, currentKey, cancellationToken);

        public Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default) =>
            inner.OpenBankWithKeyAsync(key, cancellationToken);

        public Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default) =>
            inner.OpenBankSkippingEnsureAsync(cancellationToken);

        private void Trace(SqliteConnection connection)
        {
            lock (_gate)
            {
                if (_hooks.ContainsKey(connection))
                {
                    return;
                }

                SQLitePCL.strdelegate_trace hook = (_, sql) => onStatement(sql);
                _hooks[connection] = hook;
                SQLitePCL.raw.sqlite3_trace(connection.Handle, hook, null);
            }
        }
    }
}
