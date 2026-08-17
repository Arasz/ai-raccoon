using AiRaccoon.Tests.Unit.Retrieval;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     The retrieval gate for tabular content (docs/adr/0077 follow-up). Everything ADR-0077 named as
///     missing is here: expected documents that contain tables, a bank re-chunked on every run rather
///     than read from a committed fixture, and a response variable anchored on the answer span.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class TableRetrievalGateTests(TableCorpusFixture fixture, ITestOutputHelper output)
    : IClassFixture<TableCorpusFixture>
{
    private const int RankCutoff = 5;
    private const int SearchLimit = 10;

    /// <summary>
    ///     Measured 2026-08-17 over 40 graded queries: mean nDCG@5 0.227007, mean MRR@10 0.237599.
    ///     Pinned below the measurement because this gate embeds live rather than replaying pinned
    ///     vectors, so the number carries hardware variance the jsaa gates do not — and above both
    ///     perturbations (reversal 0.102/0.113, mispairing 0.079/0.080), which is what makes them
    ///     able to fail. The earlier 0.050/0.070 pair was derived from a 16-query set and could not
    ///     be compared against this one (docs/adr/0081).
    /// </summary>
    private const double MeanNdcg5Floor = 0.160;

    private const double MeanMrr10Floor = 0.170;

    /// <summary>A per-query score must move by more than this to count as moved rather than jittered.</summary>
    private const double MovementTolerance = 0.01;

    /// <summary>
    ///     A quarter of the graded set. A chunking change that moves fewer scores than this is one
    ///     this corpus cannot really see, which is the blindness docs/adr/0077 recorded. Derived from
    ///     the set size, not fitted to the 19 of 40 a 128-token re-ingest actually moves.
    /// </summary>
    private static int MinimumMovedQueries => Math.Max(3, TableCorpusCatalog.Load().Count / 4);

    [Fact]
    public async Task ParaphraseRetrieval_HoldsItsPinnedFloors()
    {
        var (ndcg, mrr) = await ScoreAllAsync(fixture.Bank, reverse: false);

        output.WriteLine($"mean nDCG@5={ndcg.Average():F6} (floor {MeanNdcg5Floor}) " +
                         $"mean MRR@10={mrr.Average():F6} (floor {MeanMrr10Floor})");
        ndcg.Average().ShouldBeGreaterThanOrEqualTo(MeanNdcg5Floor);
        mrr.Average().ShouldBeGreaterThanOrEqualTo(MeanMrr10Floor);
    }

    /// <summary>
    ///     The floors discriminate. Reversal is not the perturbation to use here: it *raises* this
    ///     corpus's mean (0.071 -> 0.161 on 2026-08-17), because when the answer-bearing chunk reaches
    ///     the top 10 at all it usually sits in the bottom half — ADR-0077 saw the same on jsaa query
    ///     A8. Mispairing does discriminate: scoring each query against the next query's document must
    ///     collapse the number, or the metric is not measuring whether the right thing was retrieved.
    /// </summary>
    [Fact]
    public async Task MismatchedPairing_FailsTheFloors()
    {
        var bank = fixture.Bank;
        var queries = TableCorpusCatalog.Load();
        var ndcg = new List<double>();
        var mrr = new List<double>();

        for (var index = 0; index < queries.Count; index++)
        {
            var asked = queries[index];
            var graded = queries[(index + 1) % queries.Count];
            var relevant = bank.RelevantFor(graded);
            var ranked = await bank.RankAsync(asked.Query, SearchLimit, TestContext.Current.CancellationToken);
            ndcg.Add(RetrievalMetrics.NdcgAtK([.. ranked.Take(RankCutoff)], relevant, RankCutoff));
            mrr.Add(RetrievalMetrics.Mrr(ranked, relevant));
        }

        output.WriteLine($"mispaired mean nDCG@5={ndcg.Average():F6} mean MRR@10={mrr.Average():F6}");
        ndcg.Average().ShouldBeLessThan(MeanNdcg5Floor,
            "a floor that a query graded against the wrong document still clears is not a gate");
        mrr.Average().ShouldBeLessThan(MeanMrr10Floor,
            "a floor that a query graded against the wrong document still clears is not a gate");
    }

    /// <summary>
    ///     A second, independent perturbation. At 16 queries a reversed top-10 *outscored* the real
    ///     order, so reversal was demoted to a report; over 40 it degrades as it should
    ///     (0.102/0.113 against 0.227/0.238), which is itself evidence the old width could not
    ///     resolve this corpus (docs/adr/0081).
    /// </summary>
    [Fact]
    public async Task ReversedRanking_FailsTheFloors()
    {
        var (ndcg, mrr) = await ScoreAllAsync(fixture.Bank, reverse: false);
        var (reversedNdcg, reversedMrr) = await ScoreAllAsync(fixture.Bank, reverse: true);

        output.WriteLine($"as ranked:  nDCG@5={ndcg.Average():F6} MRR@10={mrr.Average():F6}");
        output.WriteLine($"reversed:   nDCG@5={reversedNdcg.Average():F6} MRR@10={reversedMrr.Average():F6}");
        reversedNdcg.Average().ShouldBeLessThan(MeanNdcg5Floor, "a floor a reversal survives is not a gate");
        reversedMrr.Average().ShouldBeLessThan(MeanMrr10Floor, "a floor a reversal survives is not a gate");
    }

    /// <summary>
    ///     The property ADR-0077 could not obtain: this gate can see a chunking change. The committed
    ///     jsaa fixture re-chunks nothing, so its floors were invariant under the change under test and
    ///     "the gate goes green having measured nothing". Re-ingesting the same documents at a
    ///     different chunk budget must move the per-query scores.
    /// </summary>
    [Fact]
    public async Task ADifferentChunkBudget_MovesTheScores()
    {
        var baseline = await ScoreAllAsync(fixture.Bank, reverse: false);
        await using var rechunked = await TableCorpusBank.BuildAsync(128, TestContext.Current.CancellationToken);
        var perturbed = await ScoreAllAsync(rechunked, reverse: false);

        rechunked.Chunks.Count.ShouldBeGreaterThan(fixture.Bank.Chunks.Count,
            "a smaller chunk budget must produce more chunks, or the perturbation did not apply");

        var moved = baseline.Ndcg
            .Select((score, index) => Math.Abs(score - perturbed.Ndcg[index]))
            .Count(delta => delta > MovementTolerance);

        output.WriteLine($"{fixture.Bank.Chunks.Count} chunks -> {rechunked.Chunks.Count} chunks; " +
                         $"{moved}/{baseline.Ndcg.Count} per-query nDCG@5 scores moved");
        moved.ShouldBeGreaterThanOrEqualTo(MinimumMovedQueries,
            "a chunking change must move this corpus's scores — a gate that cannot see the change " +
            "under test measures nothing (docs/adr/0077)");
    }

    /// <summary>
    ///     What makes the response variable adjudicable. Two of ADR-0077's arms multiply the units
    ///     containing the answer, which inflates any rank-of-any-match metric mechanically. A span
    ///     anchor does not make the set constant — the 48-token overlay still copies a span into
    ///     adjacent chunks, so cutting the corpus finer can take a set from 1 to 3 — but it keeps the
    ///     set bounded and tiny while whole-file relevance, the metric this replaces, grows with the
    ///     chunk count. The bound is the property; report the sizes so an inflating arm is visible.
    /// </summary>
    [Fact]
    public async Task SpanAnchoredRelevance_StaysBoundedWhileWholeFileRelevanceGrows()
    {
        await using var fragmented = await TableCorpusBank.BuildAsync(64, TestContext.Current.CancellationToken);

        var growth = (double)fragmented.Chunks.Count / fixture.Bank.Chunks.Count;
        growth.ShouldBeGreaterThan(3.0, "the 64-token budget must fragment the corpus for this to prove anything");

        var spanSizes = new List<int>();
        var fileGrowth = new List<double>();
        foreach (var query in TableCorpusCatalog.Load())
        {
            var span = fragmented.RelevantFor(query).Count;
            var fileBefore = fixture.Bank.Chunks.Count(chunk =>
                SpanAnchoredRelevance.IsFromFile(chunk.SourceFile, query.ExpectedSource));
            var fileAfter = fragmented.Chunks.Count(chunk =>
                SpanAnchoredRelevance.IsFromFile(chunk.SourceFile, query.ExpectedSource));
            spanSizes.Add(span);
            fileGrowth.Add((double)fileAfter / fileBefore);
            output.WriteLine($"{query.Id}: span-anchored {fixture.Bank.RelevantFor(query).Count} -> {span}; " +
                             $"whole-file {fileBefore} -> {fileAfter}");
        }

        output.WriteLine($"corpus grew {growth:F1}x; largest span-anchored set {spanSizes.Max()}; " +
                         $"mean whole-file growth {fileGrowth.Average():F1}x");
        spanSizes.Max().ShouldBeLessThanOrEqualTo(4,
            "a span-anchored relevance set must stay a handful of chunks however finely the corpus is cut");
        fileGrowth.Average().ShouldBeGreaterThan(spanSizes.Max(),
            "whole-file relevance — the metric this replaces — must be the one that scales with the chunk count");
    }

    /// <summary>
    ///     Smoke check on the harness rather than the ranking: searching a chunk's own text must find
    ///     it. It is not 1.0 — a chunk-length query is truncated to the embedding budget and dilutes
    ///     FTS — so this catches a broken index, not a ranking regression.
    /// </summary>
    [Fact]
    public async Task SearchingAChunksOwnText_FindsThatChunk()
    {
        var bank = fixture.Bank;
        var scores = new List<double>();

        foreach (var query in TableCorpusCatalog.Load())
        {
            var relevant = bank.RelevantFor(query);
            var anchor = bank.Chunks.First(chunk => relevant.Contains(chunk.Hash));
            var ranked = await bank.RankAsync(anchor.Value, SearchLimit, TestContext.Current.CancellationToken);
            scores.Add(RetrievalMetrics.Mrr(ranked, relevant));
        }

        output.WriteLine($"identity mean MRR={scores.Average():F6}");
        scores.Average().ShouldBeGreaterThan(0.5, "the corpus is not indexed or not searchable");
    }

    [Fact]
    public async Task ReportPerQueryScores()
    {
        var bank = fixture.Bank;
        output.WriteLine($"corpus: {bank.Chunks.Count} chunks at maxTokens={bank.MaxTokens}");

        foreach (var query in TableCorpusCatalog.Load())
        {
            var relevant = bank.RelevantFor(query);
            var ranked = await bank.RankAsync(query.Query, SearchLimit, TestContext.Current.CancellationToken);
            var anchor = bank.Chunks.First(chunk => relevant.Contains(chunk.Hash));
            var tableChars = TableCorpusCatalog.TableLines(anchor.Value).Sum(line => line.Length + 1);
            output.WriteLine(
                $"{query.Id}: nDCG@5={RetrievalMetrics.NdcgAtK([.. ranked.Take(RankCutoff)], relevant, RankCutoff):F4} " +
                $"MRR@10={RetrievalMetrics.Mrr(ranked, relevant):F4} relevant={relevant.Count} " +
                $"anchorTableShare={Math.Min(1.0, (double)tableChars / anchor.Value.Length):F2} {query.ExpectedSource}");
        }
    }

    private async Task<(List<double> Ndcg, List<double> Mrr)> ScoreAllAsync(TableCorpusBank bank, bool reverse)
    {
        var ndcg = new List<double>();
        var mrr = new List<double>();

        foreach (var query in TableCorpusCatalog.Load())
        {
            var relevant = bank.RelevantFor(query);
            var ranked = (await bank.RankAsync(query.Query, SearchLimit, TestContext.Current.CancellationToken)).ToList();
            if (reverse)
            {
                ranked.Reverse();
            }

            ndcg.Add(RetrievalMetrics.NdcgAtK([.. ranked.Take(RankCutoff)], relevant, RankCutoff));
            mrr.Add(RetrievalMetrics.Mrr(ranked, relevant));
        }

        return (ndcg, mrr);
    }
}

/// <summary>Builds the re-chunked table corpus once for the gate — the ingest embeds every chunk with
/// the bundled model, so it is paid once per class rather than once per fact.</summary>
public sealed class TableCorpusFixture : IAsyncLifetime
{
    private TableCorpusBank? _bank;

    internal TableCorpusBank Bank => _bank ?? throw new InvalidOperationException("table corpus was not built");

    public async ValueTask InitializeAsync() =>
        _bank = await TableCorpusBank.BuildAsync(null, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_bank is not null)
        {
            await _bank.DisposeAsync();
        }
    }
}
