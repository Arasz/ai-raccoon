using System.Runtime.InteropServices;
using System.Text;
using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Tests.Unit.Retrieval;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Parity gate: the managed store is measured against the vendored reference golden output on
///     the shared corpus; PASS requires no nDCG regression beyond NdcgParityDelta at any sweep point
///     (docs/work/features-native-memory/native-memory.feature). p95 latency is a separate
///     Performance=Benchmark fact so host speed cannot fail the correctness gate.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection("managed-parity")]
public sealed class ParityGateTests(ManagedHarnessFixture fixture, ITestOutputHelper output)
{
    /// <summary>Maximum allowed regression of new-side nDCG@10 below the reference at one sweep point.</summary>
    public const double NdcgParityDelta = 0.02;

    /// <summary>Documented p95 budget for one managed query at corpus size (embed + FTS5 + vec0 + fusion).</summary>
    public const double P95LatencyBudgetMs = 1000.0;

    public const string WriteReportEnvVar = "AIRACCOON_HARNESS_WRITE_REPORT";

    [Fact]
    public async Task FusedSearch_NdcgParityWithinDelta_AtEverySweepPoint()
    {
        fixture.Harness.DocumentCount.ShouldBe(RealWorldCorpus.Documents.Count,
            "the managed harness must hold the full shared corpus");

        var golden = GoldenFile.Load(Path.Combine(ReferenceAssets.AssetsDirectory, GoldenFile.FileName));
        var reference = AggregateFromGolden(golden);

        var outcomes = await fixture.EnsureSweptAsync(TestContext.Current.CancellationToken);

        var rows = new StringBuilder();
        var regressions = new List<string>();
        var bestDelta = double.MaxValue;
        var worstDelta = double.MinValue;
        foreach (var outcome in outcomes)
        {
            var delta = outcome.NdcgAt10 - reference.NdcgAt10;
            bestDelta = Math.Min(bestDelta, delta);
            worstDelta = Math.Max(worstDelta, delta);
            rows.AppendLine(
                $"| {outcome.Point.Id} | {outcome.NdcgAt10:F4} | {outcome.NdcgAt20:F4} | {outcome.Mrr:F4} | {outcome.RecallAt10:F4} | {outcome.RecallAt30:F4} | {delta:+0.0000;-0.0000} |");
            if (delta < -NdcgParityDelta)
            {
                regressions.Add($"{outcome.Point.Id}: new-side nDCG@10 {outcome.NdcgAt10:F4} is {Math.Abs(delta):F4} below reference {reference.NdcgAt10:F4} (regression > {NdcgParityDelta:F2})");
            }
        }

        regressions.ShouldBeEmpty(
            $"{regressions.Count} sweep point(s) regressed more than {NdcgParityDelta:F2} below the reference:\n{string.Join("\n", regressions)}");
        output.WriteLine(
            $"observed nDCG@10 delta range across all sweep points: {bestDelta:+0.0000;-0.0000} .. {worstDelta:+0.0000;-0.0000} (positive = new side above the reference)");

        // p95 latency is reported here, not asserted: the wall-clock budget is a separate
        // Performance=Benchmark fact (FusedSearch_P95LatencyWithinBudget) so a slow host cannot
        // fail this correctness gate (owner gate G7; ruling #464). Scoped to this sweep's own
        // samples (fixture.SweepQueryLatenciesMs), not the shared Harness.QueryLatenciesMs pool
        // other facts/helpers also append to.
        var p95 = TestData.Percentile(fixture.SweepQueryLatenciesMs, 0.95);

        output.WriteLine($"reference (golden k={golden.K}): nDCG@10 {reference.NdcgAt10:F4}, MRR {reference.Mrr:F4}, Recall@10 {reference.RecallAt10:F4}");
        output.WriteLine($"new-side p95 {p95:F1} ms / p50 {TestData.Percentile(fixture.SweepQueryLatenciesMs, 0.50):F1} ms over {fixture.SweepQueryLatenciesMs.Count} queries");
        output.WriteLine(rows.ToString());

        WriteReportIfRequested(golden, reference, outcomes, p95);
    }

    /// <summary>
    ///     Wall-clock companion to <see cref="FusedSearch_NdcgParityWithinDelta_AtEverySweepPoint"/>: asserts
    ///     the sweep's p95 query latency against <see cref="P95LatencyBudgetMs"/>. Split out (owner gate G7,
    ///     ruling #464) so a slow host cannot turn the nDCG correctness gate red. Calls
    ///     <see cref="ManagedHarnessFixture.EnsureSweptAsync"/> itself and reads only
    ///     <see cref="ManagedHarnessFixture.SweepQueryLatenciesMs"/> (that sweep's own samples, not the
    ///     shared <see cref="ManagedHarness.QueryLatenciesMs"/> pool other facts also append to), so this
    ///     holds run alone (<c>--filter "Performance=Benchmark"</c>) or in any method order — no
    ///     <c>ITestCaseOrderer</c> required, and the sweep executes at most once per fixture regardless of
    ///     which fact triggers it first.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Performance, TestCategories.Benchmark)]
    public async Task FusedSearch_P95LatencyWithinBudget()
    {
        await fixture.EnsureSweptAsync(TestContext.Current.CancellationToken);

        fixture.SweepQueryLatenciesMs.Count.ShouldBeGreaterThan(0,
            "p95 fact ran without a populated sweep");

        var p95 = TestData.Percentile(fixture.SweepQueryLatenciesMs, 0.95);
        p95.ShouldBeLessThanOrEqualTo(P95LatencyBudgetMs,
            $"p95 managed query latency {p95:F1} ms exceeds the {P95LatencyBudgetMs:F0} ms budget over " +
            $"{fixture.SweepQueryLatenciesMs.Count} queries");
    }

    [Fact]
    public async Task DegenerateQuerySubset_NoRegression_AtDefaultFusionConfig()
    {
        var golden = GoldenFile.Load(Path.Combine(ReferenceAssets.AssetsDirectory, GoldenFile.FileName));
        var perQueryReference = PerQueryFromGolden(golden);

        // Degenerate subset: queries with exactly one relevant doc — exact-restatement and
        // unique-recall queries where the keyword modality carries the signal.
        var degenerate = RealWorldQueries.Queries.Where(q => q.RelevantDocIds.Count == 1).ToList();
        degenerate.ShouldNotBeEmpty();

        var subsetNdcg = new List<double>();
        foreach (var query in degenerate)
        {
            var ranked = await fixture.Harness.RankAsync(ManagedHarness.DefaultPoint, query,
                TestContext.Current.CancellationToken);
            var relevant = query.RelevantDocIds.ToHashSet(StringComparer.Ordinal);
            var referenceMrr = perQueryReference[query.Id].Mrr;
            var newMrr = RetrievalMetrics.Mrr(ranked, relevant);

            // A doc the reference found in its top-10 must not be lost by the new side.
            if (referenceMrr > 0)
            {
                newMrr.ShouldBeGreaterThan(0,
                    $"{query.Id}: reference MRR {referenceMrr:F4} but new side lost the relevant doc");
            }

            subsetNdcg.Add(RetrievalMetrics.NdcgAtK(ranked, relevant, 10));
        }

        var referenceSubsetNdcg = degenerate.Average(q => perQueryReference[q.Id].Ndcg10);
        var delta = Math.Abs(subsetNdcg.Average() - referenceSubsetNdcg);
        output.WriteLine($"degenerate subset ({degenerate.Count} queries): new-side nDCG@10 {subsetNdcg.Average():F4} vs reference {referenceSubsetNdcg:F4} (delta {delta:F4})");
        delta.ShouldBeLessThanOrEqualTo(NdcgParityDelta,
            $"degenerate subset nDCG@10 {subsetNdcg.Average():F4} vs reference {referenceSubsetNdcg:F4} (delta {delta:F4} > {NdcgParityDelta:F2})");
    }

    private static AggregateMetrics AggregateFromGolden(GoldenFile golden)
    {
        var queries = RealWorldQueries.Queries.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var ndcg10 = new List<double>(golden.Queries.Count);
        var ndcg20 = new List<double>(golden.Queries.Count);
        var mrr = new List<double>(golden.Queries.Count);
        var recall10 = new List<double>(golden.Queries.Count);
        foreach (var query in golden.Queries)
        {
            if (!queries.TryGetValue(query.Id, out var corpus))
            {
                continue;
            }

            var ranked = query.Hits.Select(hit => hit.Path).ToList();
            var relevant = corpus.RelevantDocIds.ToHashSet(StringComparer.Ordinal);
            ndcg10.Add(RetrievalMetrics.NdcgAtK(ranked, relevant, 10));
            ndcg20.Add(RetrievalMetrics.NdcgAtK(ranked, relevant, 20));
            mrr.Add(RetrievalMetrics.Mrr(ranked, relevant));
            recall10.Add(RetrievalMetrics.RecallAtK(ranked, relevant, 10));
        }

        return new AggregateMetrics(ndcg10.Average(), ndcg20.Average(), mrr.Average(), recall10.Average());
    }

    private static IReadOnlyDictionary<string, QueryMetrics> PerQueryFromGolden(GoldenFile golden)
    {
        var queries = RealWorldQueries.Queries.ToDictionary(q => q.Id, StringComparer.Ordinal);
        var perQuery = new Dictionary<string, QueryMetrics>(StringComparer.Ordinal);
        foreach (var query in golden.Queries)
        {
            if (!queries.TryGetValue(query.Id, out var corpus))
            {
                continue;
            }

            var ranked = query.Hits.Select(hit => hit.Path).ToList();
            var relevant = corpus.RelevantDocIds.ToHashSet(StringComparer.Ordinal);
            perQuery[query.Id] = new QueryMetrics(
                RetrievalMetrics.NdcgAtK(ranked, relevant, 10), RetrievalMetrics.Mrr(ranked, relevant));
        }

        return perQuery;
    }

    private void WriteReportIfRequested(GoldenFile golden, AggregateMetrics reference,
        IReadOnlyList<SweepOutcome> outcomes, double p95)
    {
        if (Environment.GetEnvironmentVariable(WriteReportEnvVar) != "1")
        {
            return;
        }

        var managedDir = ResolveManagedDirectory();
        var report = new StringBuilder();
        report.AppendLine("# Retrieval parity report (P6 FR-NM-5)");
        report.AppendLine();
        report.AppendLine($"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} on {Environment.OSVersion} ({RuntimeInformation.OSArchitecture})");
        report.AppendLine(
            $"{$"- Reference oracle: {golden.Engine}, model {golden.Model} (SHA-256 {golden.ModelSha256[..12]}…), golden k={golden.K}, {golden.DocumentCount} docs, "}{golden.Queries.Count} graded queries");
        report.AppendLine("- New side: managed store (FTS5 + vec0), bundled int8 ONNX all-MiniLM-L6-v2, " +
                          "RRF fusion, rank depth 60, minScore 0 (full capture)");
        report.AppendLine("- Gate: one-sided 'no regression' — new-side nDCG@10 must not fall more than 0.02 " +
                          "below the reference at any sweep point. Positive Δ means the new side exceeds the " +
                          "reference (the sweep's purpose); the full sweep matrix is recorded here.");
        report.AppendLine();
        report.AppendLine("## Reference (vendored golden, k=10 window)");
        report.AppendLine();
        report.AppendLine("| nDCG@10 | nDCG@20* | MRR | Recall@10 |");
        report.AppendLine($"|---|---|---|---|");
        report.AppendLine($"| {reference.NdcgAt10:F4} | {reference.NdcgAt20:F4} | {reference.Mrr:F4} | {reference.RecallAt10:F4} |");
        report.AppendLine();
        report.AppendLine("*nDCG@20 is computed over the golden's k=10 window and is a lower-bound reference.");
        report.AppendLine();
        report.AppendLine("## Sweep matrix (new side), Δ = new-side nDCG@10 − reference nDCG@10");
        report.AppendLine();
        report.AppendLine("| point | nDCG@10 | nDCG@20 | MRR | Recall@10 | Recall@30 | Δ nDCG@10 |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var outcome in outcomes)
        {
            var delta = outcome.NdcgAt10 - reference.NdcgAt10;
            report.AppendLine(
                $"| {outcome.Point.Id} | {outcome.NdcgAt10:F4} | {outcome.NdcgAt20:F4} | {outcome.Mrr:F4} | {outcome.RecallAt10:F4} | {outcome.RecallAt30:F4} | {delta:+0.0000;-0.0000} |");
        }

        report.AppendLine();
        report.AppendLine("## Per-query audit at the default config (k60, weights 1:1)");
        report.AppendLine();
        report.AppendLine("For each graded query: new-side nDCG@10 minus reference nDCG@10. Queries where the new " +
                          "side is worse by more than 0.02 are true regressions; everything else is parity or better.");
        report.AppendLine();
        var audit = PerQueryAudit(golden);
        report.AppendLine($"- Queries with new side better than reference by > 0.02: {audit.Better}");
        report.AppendLine($"- Queries within ±0.02 of the reference: {audit.Within}");
        report.AppendLine($"- Queries with new side worse than reference by > 0.02 (regressions): {audit.Worse}");
        report.AppendLine($"- Worst per-query regression: {audit.WorstRegression:+0.0000;-0.0000} ({audit.WorstQueryId})");
        report.AppendLine();
        report.AppendLine("### Regressing queries (default config), modality attribution");
        report.AppendLine();
        report.AppendLine("For each query where the new side falls more than 0.02 below the reference, the table " +
                          "shows reference nDCG@10, new-side nDCG@10 (default), and the new side with only one " +
                          "modality (FTS-only = weights 1:0, vec-only = weights 0:1) to attribute the loss.");
        report.AppendLine();
        report.AppendLine("| query | reference | new (default) | FTS-only | vec-only |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var row in audit.RegressionRows)
        {
            report.AppendLine($"| {row.QueryId} | {row.Reference:F4} | {row.Default:F4} | {row.FtsOnly:F4} | {row.VecOnly:F4} |");
        }

        report.AppendLine();
        report.AppendLine("## Latency (new side, per query)");
        report.AppendLine();
        report.AppendLine($"| p50 | p95 | max | samples |");
        report.AppendLine($"|---|---|---|---|");
        report.AppendLine(
            $"{$"| {TestData.Percentile(fixture.SweepQueryLatenciesMs, 0.50):F1} ms | {p95:F1} ms | {(fixture.SweepQueryLatenciesMs.Count == 0 ? 0 : fixture.SweepQueryLatenciesMs.Max()):F1} ms | "}{fixture.SweepQueryLatenciesMs.Count} |");

        Directory.CreateDirectory(managedDir);
        File.WriteAllText(Path.Combine(managedDir, "parity-report.md"), report.ToString());
        output.WriteLine($"wrote {Path.Combine(managedDir, "parity-report.md")}");
    }

    private AuditResult PerQueryAudit(GoldenFile golden)
    {
        var better = 0;
        var within = 0;
        var worse = 0;
        var worstRegression = 0.0;
        var worstQueryId = "";
        var regressionRows = new List<RegressionRow>();
        foreach (var query in RealWorldQueries.Queries)
        {
            var relevant = query.RelevantDocIds.ToHashSet(StringComparer.Ordinal);
            var referenceNdcg = golden.Queries.Single(q => q.Id == query.Id).Hits
                .Select(hit => hit.Path).ToList();
            var referenceValue = RetrievalMetrics.NdcgAtK(referenceNdcg, relevant, 10);
            var newRanked = fixture.Harness.RankAsync(ManagedHarness.DefaultPoint, query,
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            var newValue = RetrievalMetrics.NdcgAtK(newRanked, relevant, 10);
            var delta = newValue - referenceValue;
            if (delta > NdcgParityDelta)
            {
                better++;
            }
            else if (delta < -NdcgParityDelta)
            {
                worse++;
                if (delta < worstRegression)
                {
                    worstRegression = delta;
                    worstQueryId = query.Id;
                }

                // Attribute the loss: single-modality rankings show whether the keyword or vector
                // modality accounts for the drop below the reference.
                var ftsOnly = fixture.Harness.RankAsync(
                    new SweepPoint(60, 1, 0), query, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                var vecOnly = fixture.Harness.RankAsync(
                    new SweepPoint(60, 0, 1), query, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                regressionRows.Add(new RegressionRow(query.Id, referenceValue, newValue,
                    RetrievalMetrics.NdcgAtK(ftsOnly, relevant, 10),
                    RetrievalMetrics.NdcgAtK(vecOnly, relevant, 10)));
            }
            else
            {
                within++;
            }
        }

        return new AuditResult(better, within, worse, worstRegression, worstQueryId, regressionRows);
    }

    private static string ResolveManagedDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var assets = Path.Combine(dir.FullName, "tests", "AiRaccoon.Tests", "Unit", "Retrieval", "assets");
            if (File.Exists(Path.Combine(assets, ReferenceAssets.ManifestFileName)))
            {
                return Path.Combine(dir.FullName, "tests", "AiRaccoon.Tests", "Unit", "Retrieval", "Managed");
            }
        }

        return AppContext.BaseDirectory;
    }

    private sealed record AggregateMetrics(double NdcgAt10, double NdcgAt20, double Mrr, double RecallAt10);

    private sealed record QueryMetrics(double Ndcg10, double Mrr);

    private sealed record AuditResult(
        int Better,
        int Within,
        int Worse,
        double WorstRegression,
        string WorstQueryId,
        IReadOnlyList<RegressionRow> RegressionRows);

    private sealed record RegressionRow(string QueryId, double Reference, double Default, double FtsOnly, double VecOnly);
}
