using AiRaccoon.Core.Chunking;
using AiRaccoon.Tests.Unit.Retrieval;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     Scores ADR-0077's two surviving tuning arms against the table corpus, on the same documents,
///     queries and search path. Reports; asserts nothing about which arm wins — the numbers are the
///     evidence a later record decides on, and relevance-set sizes are reported beside them because
///     a span anchor bounds inflation without freezing it (docs/adr/0079).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class TableChunkingArmComparison(ITestOutputHelper output)
{
    private static readonly (string Name, Func<TokenCount, IMarkdownChunker>? Arm)[] Arms =
    [
        ("shipped (whole table, header carry-over)", null),
        ("per-row + header carry-over", TableChunkingArms.PerRow),
        ("row linearised into sentences", TableChunkingArms.Linearised),
        ("whole table + section heading", TableChunkingArms.WholeTableWithHeading),
        ("per-row + section heading", TableChunkingArms.PerRowWithHeading),
        ("linearised + section heading", TableChunkingArms.LinearisedWithHeading)
    ];

    [Fact]
    public async Task ScoreEveryArm()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var queries = TableCorpusCatalog.Load();

        foreach (var (name, arm) in Arms)
        {
            await using var bank = await TableCorpusBank.BuildAsync(null, arm, cancellationToken);
            var ndcg = new List<double>();
            var mrr = new List<double>();
            var relevantSizes = new List<int>();
            var unanchored = new List<string>();

            foreach (var query in queries)
            {
                IReadOnlySet<string> relevant;
                try
                {
                    relevant = bank.RelevantFor(query);
                }
                catch (InvalidOperationException)
                {
                    // A linearised row no longer contains the graded span verbatim. That is a real
                    // property of the arm, not a test defect: report it rather than scoring around it.
                    unanchored.Add(query.Id);
                    continue;
                }

                var ranked = await bank.RankAsync(query.Query, 10, cancellationToken);
                ndcg.Add(RetrievalMetrics.NdcgAtK([.. ranked.Take(5)], relevant, 5));
                mrr.Add(RetrievalMetrics.Mrr(ranked, relevant));
                relevantSizes.Add(relevant.Count);
            }

            var scored = ndcg.Count;
            output.WriteLine(
                $"{name}: chunks={bank.Chunks.Count} scored={scored}/{queries.Count} " +
                $"meanNdcg5={(scored > 0 ? ndcg.Average() : 0):F6} meanMrr10={(scored > 0 ? mrr.Average() : 0):F6} " +
                $"meanRelevantSet={(scored > 0 ? relevantSizes.Average() : 0):F2} maxRelevantSet={(scored > 0 ? relevantSizes.Max() : 0)}");
            if (unanchored.Count > 0)
            {
                output.WriteLine($"    span not found verbatim under this arm: {string.Join(", ", unanchored)}");
            }
        }
    }
}
