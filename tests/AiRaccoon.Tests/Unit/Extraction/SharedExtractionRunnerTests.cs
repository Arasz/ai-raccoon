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
        store.Candidates["acme"] = Enumerable.Range(0, 10)
            .Select(i => Row($"h{i:00}", $"organic fact {i} about beta"))
            .ToList();

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
        store.Candidates["acme"] = Enumerable.Range(0, 10)
            .Select(i => Row($"h{i:00}", $"organic fact {i} about beta"))
            .ToList();

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
        queue.Rows = [new PromotionQueueRow("acme", "h09", "h09.md", "stale v1 value", null, 0.1, [], 0, 0)];

        await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 3, TestContext.Current.CancellationToken);

        queue.LastCandidates!.Select(c => c.Hash).ShouldContain("h09",
            "a row already queued must be refreshed even though it now ranks outside the display limit");
        queue.LastCandidates!.Count.ShouldBe(4,
            "3 new top-ranked candidates plus the 1 already-queued refresh — not the whole eligible pool");
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
