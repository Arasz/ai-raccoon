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
        var value = new string('x', 400) + " beta";
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

    /// <summary>Recency scoring reads the injected clock, not the wall clock.</summary>
    [Fact]
    public async Task ProposeAsync_ScoresRecencyAgainstTheInjectedClock()
    {
        var (store, _, time, runner) = NewStack();
        store.Candidates["acme"] = [Row("h1", ageDays: 20)];

        var fresh = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);
        fresh[0].Reasons.ShouldContain("recent");

        time.Advance(TimeSpan.FromDays(30));

        var stale = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 20, TestContext.Current.CancellationToken);
        stale[0].Reasons.ShouldNotContain("recent");
    }

    [Fact]
    public async Task ProposeAsync_HonoursTheLimit()
    {
        var (store, queue, _, runner) = NewStack();
        store.Candidates["acme"] = Enumerable.Range(0, 10)
            .Select(i => Row($"h{i:00}", $"organic fact {i} about beta"))
            .ToList();

        var candidates = await runner.ProposeAsync("acme", EmptyIndex,
            includeTtlRows: false, limit: 3, TestContext.Current.CancellationToken);

        candidates.Count.ShouldBe(3);
        queue.LastCandidates!.Count.ShouldBe(3);
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
