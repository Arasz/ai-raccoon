using AiRaccoon.Core.Memory;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SharedExtractionRunnerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly SharedIndex EmptyIndex = new([], []);

    private static (FakeExtractionStore Store, FakePromotionQueue Queue, FakeTimeProvider Time,
        SharedExtractionRunner Runner) NewStack()
    {
        var store = new FakeExtractionStore();
        var queue = new FakePromotionQueue();
        var time = new FakeTimeProvider(FixedNow);
        return (store, queue, time, new SharedExtractionRunner(store, new SharedExtractionService(), queue, time));
    }

    private static ExtractionCandidateRow Row(string hash, string value = "organic fact about beta",
        string? sourceFile = null, int ageDays = 5) =>
        new(hash, $"{hash}.md", value, sourceFile, 0.5, 0, FixedNow.AddDays(-ageDays), null);

    [Fact]
    public async Task ProposeAsync_QueuesTheRankedCandidates()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [Row("h1")];

        var candidates = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        candidates.Select(c => c.Hash).ShouldBe(["h1"]);
        queue.LastProject.ShouldBe("acme");
        queue.LastCandidates!.Select(c => c.Hash).ShouldBe(["h1"]);
    }

    /// <summary>The preview-only ShareCandidate is joined back to its row so the queue keeps the full value.</summary>
    [Fact]
    public async Task ProposeAsync_QueuesTheFullValueAndSourceFile()
    {
        var (store, queue, _, runner) = NewStack();
        var value = string.Join(" ", Enumerable.Repeat("Documented migration step details for the record.", 30)) +
                    " beta";
        store.Candidates["acme"] = [Row("h1", value, sourceFile: "docs/beta.md")];

        var candidates = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        candidates[0].ValuePreview.Length.ShouldBeLessThan(value.Length);
        var queued = queue.LastCandidates!.Single();
        queued.Value.ShouldBe(value);
        queued.SourceFile.ShouldBe("docs/beta.md");
        queued.Score.ShouldBe(candidates[0].Score);
    }

    [Fact]
    public async Task ProposeAsync_NoCandidates_LeavesTheQueueUntouched()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [];

        var candidates = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        candidates.ShouldBeEmpty();
        queue.LastProject.ShouldBeNull();
    }

    /// <summary>v2: recency is a sort tie-break only, never part of the score (docs/adr/0018-promotion-scoring-v2.md)
    /// — a re-propose after time passes must not change an unchanged row's score.</summary>
    [Fact]
    public async Task ProposeAsync_ScoreIsStableAcrossTime()
    {
        var (store, _, time, runner) = NewStack();
        store.Candidates["acme"] = [Row("h1", ageDays: 20)];

        var fresh = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromDays(30));

        var stale = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        stale[0].Score.ShouldBe(fresh[0].Score);
    }

    [Fact]
    public async Task ProposeAsync_HonoursTheLimit_ForTheReturnedCandidates()
    {
        var (store, _, _, runner) = NewStack();
        store.Candidates["acme"] =
        [
            .. Enumerable.Range(0, 10)
                .Select(i => Row($"h{i:00}", $"organic fact {i} about beta"))
        ];

        var candidates = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 3, TestContext.Current.CancellationToken);

        candidates.Count.ShouldBe(3);
    }

    /// <summary>A brand-new candidate that is not already queued is subject to the display limit —
    /// otherwise one pass over the whole eligible pool floods the queue toward capacity (the entire
    /// pool, not just `limit` rows, would be inserted as new).</summary>
    [Fact]
    public async Task ProposeAsync_BoundsBrandNewCandidatesToTheDisplayLimit()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] =
        [
            .. Enumerable.Range(0, 10)
                .Select(i => Row($"h{i:00}", $"organic fact {i} about beta"))
        ];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 3, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Count.ShouldBe(3,
            "candidates not already queued must not exceed the display limit in a single pass");
    }

    /// <summary>The display limit and the re-score set are not the same thing — a row already
    /// queued still gets its score refreshed on every pass even when it now ranks outside the top
    /// `limit`, without that forcing every OTHER eligible row into the queue too (the regression:
    /// re-scoring the existing queue was conflated with inserting the entire eligible pool).</summary>
    [Fact]
    public async Task ProposeAsync_RefreshesAnAlreadyQueuedCandidate_EvenWhenItRanksOutsideTheDisplayLimit()
    {
        var (store, queue, _, runner) = NewStack();
        var rows = Enumerable.Range(0, 10)
            .Select(i => Row($"h{i:00}", $"organic fact {i} about beta"))
            .ToList();
        // Oldest of an otherwise score-tied set (recency is a tie-break only), so RankAll sorts it
        // last — reliably outside limit: 3.
        rows[9] = Row("h09", "organic fact 9 about beta", ageDays: 999);
        store.Candidates["acme"] = rows;
        queue.Rows =
            [new PromotionQueueRow("acme", "h09", "h09.md", "queued value", null, 0.1, [], 0, 0, PromotionScorer.Version)];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 3, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Select(c => c.Hash).ShouldContain("h09",
            "a row already queued must be refreshed even though it now ranks outside the display limit");
        queue.LastCandidates!.Count.ShouldBe(4,
            "3 new top-ranked candidates plus the 1 already-queued refresh — not the whole eligible pool");
    }

    /// <summary>The defect this cap fixes: one document's chunks must not flood the queue in a single
    /// pass (docs/work/2026-08-09-promotion-scoring-measurement.md — 33 slots from one document).</summary>
    [Fact]
    public async Task ProposeAsync_AppliesThePerDocumentCap_ToNewlyQueuedCandidates()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] =
        [
            .. Enumerable.Range(0, 5)
                .Select(i => Row($"h{i:00}", $"organic fact {i} about beta", sourceFile: "doc.md"))
        ];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Count.ShouldBe(SharedExtractionService.MaxQueuedPerSourceDocument,
            "one document may not occupy more than the per-document cap of the queue in a single pass");
    }

    /// <summary>Counting must look at the whole queue, not just this pass — otherwise each pass admits
    /// `cap` more and the accumulation defect survives across passes.</summary>
    [Fact]
    public async Task ProposeAsync_DocumentAlreadyAtCapInTheQueue_AdmitsNoNewRowsFromThatDocument()
    {
        var (store, queue, _, runner) = NewStack();
        queue.Rows =
        [
            new PromotionQueueRow("acme", "q1", "q1.md", "v", "doc.md", 0.1, [], 0, 0, PromotionScorer.Version),
            new PromotionQueueRow("acme", "q2", "q2.md", "v", "doc.md", 0.1, [], 0, 0, PromotionScorer.Version),
            new PromotionQueueRow("acme", "q3", "q3.md", "v", "doc.md", 0.1, [], 0, 0, PromotionScorer.Version)
        ];
        store.Candidates["acme"] = [Row("new1", "organic fact about beta", sourceFile: "doc.md")];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.LastProject.ShouldBeNull("the only new candidate belongs to an already-capped document, "
                                       + "and none of the already-queued rows are in this pass's eligible pool to refresh");
    }

    /// <summary>Regression pin for the refresh stream: rows already in the queue keep being refreshed
    /// every pass regardless of the per-document cap, even when their document is already at or over
    /// it — refreshing and capping new admissions are different operations.</summary>
    [Fact]
    public async Task ProposeAsync_RefreshesQueuedRows_EvenWhenTheirDocumentIsAtTheCap()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] =
        [
            Row("q1", "organic fact 1 about beta", sourceFile: "doc.md"),
            Row("q2", "organic fact 2 about beta", sourceFile: "doc.md"),
            Row("q3", "organic fact 3 about beta", sourceFile: "doc.md")
        ];
        queue.Rows =
        [
            new PromotionQueueRow("acme", "q1", "q1.md", "v", "doc.md", 0.1, [], 0, 0, PromotionScorer.Version),
            new PromotionQueueRow("acme", "q2", "q2.md", "v", "doc.md", 0.1, [], 0, 0, PromotionScorer.Version),
            new PromotionQueueRow("acme", "q3", "q3.md", "v", "doc.md", 0.1, [], 0, 0, PromotionScorer.Version)
        ];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Select(c => c.Hash).ShouldBe(["q1", "q2", "q3"], ignoreOrder: true,
            "already-queued rows must still be refreshed even though their document is at the cap");
    }

    [Fact]
    public async Task ProposeAsync_SkipsRowsAlreadyShared()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [Row("h1")];

        var candidates = await runner.ProposeAsync("acme", new SharedIndex([], ["shared/h1.md"]),
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        candidates.ShouldBeEmpty();
        queue.LastProject.ShouldBeNull();
    }

    // ------------------------------------------------------------------ scorer-version auto-clear (ADR-0018)
    /// <summary>The defect this fixes: rows scored by a retired scorer version outrank correctly-scored
    /// current rows on the eviction floor and never age out on their own. ClearStaleAsync runs before
    /// ranking on every pass — a row on an old version is gone; a row already on the current one is not.</summary>
    [Fact]
    public async Task ProposeAsync_ClearsQueuedRows_WithAnOlderScorerVersion_ButKeepsTheCurrentOne()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [];
        queue.Rows =
        [
            new PromotionQueueRow("acme", "old", "old.md", "v1-scored value", null, 2.5,
                ["cross-project", "recent"], 0, 0, ScorerVersion: 0),
            new PromotionQueueRow("acme", "current", "current.md", "current value", null, 1.0,
                [], 0, 0, ScorerVersion: PromotionScorer.Version)
        ];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.Rows.Select(r => r.Hash).ShouldBe(["current"],
            "the row on a retired scorer version is cleared; the row already on the current version stays");
    }

    /// <summary>Deleting rather than re-scoring in place is deliberate: the normal propose path
    /// re-admits anything still eligible on merit in the very same pass — a clear must not
    /// permanently drop a candidate that is still good.</summary>
    [Fact]
    public async Task ProposeAsync_ReAdmitsAClearedStaleRow_WhenItIsStillAnEligibleCandidate_InTheSamePass()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [Row("h1")];
        queue.Rows =
        [
            new PromotionQueueRow("acme", "h1", "h1.md", "stale v1 value", null, 2.5,
                ["cross-project", "recent"], 0, 0, ScorerVersion: 0)
        ];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Select(c => c.Hash).ShouldContain("h1",
            "h1 is still eligible on merit, so the propose path re-admits it in the same pass after the stale clear");
    }

    /// <summary>The other side of re-admission: a stale row that is no longer an eligible candidate
    /// (e.g. it would now score below the floor) must not come back just because it used to be queued.</summary>
    [Fact]
    public async Task ProposeAsync_AStaleRowNoLongerEligible_IsGone_AndDoesNotComeBack()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [];
        queue.Rows =
        [
            new PromotionQueueRow("acme", "gone", "gone.md", "stale v1 value", null, 2.5,
                ["cross-project", "recent"], 0, 0, ScorerVersion: 0)
        ];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.Rows.ShouldBeEmpty("the stale row was cleared, and nothing in this pass re-admits it");
        queue.LastProject.ShouldBeNull("no candidate was eligible to (re-)queue");
    }

    [Fact]
    public async Task ProposeAsync_QueuesNewCandidates_WithTheCurrentScorerVersion()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [Row("h1")];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Single().ScorerVersion.ShouldBe(PromotionScorer.Version);
    }

    [Fact]
    public async Task ProposeAsync_RefreshingAnAlreadyQueuedCandidate_StampsTheCurrentScorerVersion()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = [Row("h1")];
        queue.Rows =
        [
            new PromotionQueueRow("acme", "h1", "h1.md", "old value", null, 0.1, [], 0, 0,
                ScorerVersion: PromotionScorer.Version)
        ];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Single(c => c.Hash == "h1").ScorerVersion.ShouldBe(PromotionScorer.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ProposeAsync_RejectsANonPositiveLimit(int limit)
    {
        var (_, _, _, runner) = NewStack();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            runner.ProposeAsync("acme", EmptyIndex, includeTtlRows: false, limit,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProposeAsync_RejectsABlankProjectId()
    {
        var (_, _, _, runner) = NewStack();

        await Should.ThrowAsync<ArgumentException>(() =>
            runner.ProposeAsync("  ", EmptyIndex, includeTtlRows: false, limit: 20,
                TestContext.Current.CancellationToken));
    }
}
