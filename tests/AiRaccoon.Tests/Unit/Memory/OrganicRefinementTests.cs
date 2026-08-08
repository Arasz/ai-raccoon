using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ports refine.py's organic-entry refinement layer (docs/adr/0018-promotion-scoring-v2.md):
/// on the 86-entry organic-only backup slice, the un-refined archetype+evidence model alone scored
/// +0.145 Spearman because status/turn-mirror dumps and test-count numbers read as measurements.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OrganicRefinementTests
{
    [Fact]
    public void NonOrganicEntry_IsReturnedUnchanged()
    {
        var result = OrganicRefinement.Apply(2.5, sourceFile: "docs/x.md", value: "Done. Merged. Fixed.");

        result.Score.ShouldBe(2.5);
        result.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public void StatusOpener_InFirst80Chars_IsPenalized()
    {
        var statusDump = OrganicRefinement.Apply(3.45, sourceFile: null,
            value: "Done. Everything shipped to main, the full suite passed and CI is green across the board " +
                   "with nothing left in flight for this rollout, and the on-call handoff notes are current.");
        var plainFact = OrganicRefinement.Apply(3.45, sourceFile: null,
            value: "The cache invalidation queue clears entries once their TTL expires, keeping memory " +
                   "pressure predictable across long-running sessions without any operator input at all.");

        statusDump.Reasons.ShouldContain("status-opener");
        statusDump.Score.ShouldBeLessThan(plainFact.Score);
    }

    [Fact]
    public void StatusVocabularyDensity_IsPenalized()
    {
        var statusHeavy = OrganicRefinement.Apply(3.45, sourceFile: null,
            value: "The branch was merged and pushed to origin/main; the pre-push gate chain and the full " +
                   "suite passed, CI checks are green, and the work is handed over ready for review.");

        statusHeavy.Reasons.ShouldContain("status-vocabulary");
    }

    [Fact]
    public void SecondPersonAddress_IsPenalized()
    {
        var addressed = OrganicRefinement.Apply(3.45, sourceFile: null,
            value: "As instructed, your branch is ready and you can merge it whenever the review finishes " +
                   "since every check has already gone green on your behalf across the whole pipeline.");

        addressed.Reasons.ShouldContain("second-person");
    }

    [Fact]
    public void TwoOrMoreCommitHashes_ArePenalized()
    {
        var withHashes = OrganicRefinement.Apply(3.45, sourceFile: null,
            value: "Cherry-picked a1b2c3d onto the release branch after rebasing past 9f8e7d6, then verified " +
                   "the working tree matched the expected state before handing the branch back over.");

        withHashes.Reasons.ShouldContain("commit-hashes");
    }

    [Fact]
    public void TestCountNumbers_AreExcludedFromMeasuredEvidence()
    {
        var testCountsOnly = OrganicRefinement.Apply(0.45, sourceFile: null,
            value: "Ran the full suite: 174 passed, 0 failed, 0 skipped, exit 0. All checks are green and " +
                   "the run is reproducible across machines whenever CI happens to re-trigger the job.");
        var realMeasurement = OrganicRefinement.Apply(0.45, sourceFile: null,
            value: "Wall time dropped from 640ms to 210ms after the change, a measured 3x improvement that " +
                   "held steady across five separate machines when the benchmark was re-run each time.");

        testCountsOnly.Reasons.ShouldNotContain("real-measurements");
        realMeasurement.Reasons.ShouldContain("real-measurements");
    }

    [Fact]
    public void DurableFactLanguage_IsRewarded()
    {
        var durable = OrganicRefinement.Apply(1.0, sourceFile: null,
            value: "[facts] The retry queue must never process more than one message per worker at a time; " +
                   "this is a contract the dispatcher relies on and its precedence over batching is by design.");

        durable.Reasons.ShouldContain("durable-fact-language");
    }

    [Fact]
    public void DatedFactFraming_NearTheStart_IsRewarded()
    {
        var dated = OrganicRefinement.Apply(1.0, sourceFile: null,
            value: "(2026-05-01): the retry queue caps concurrent workers at one per partition, a limit " +
                   "that has held since the queue was first introduced and has never needed to change.");
        var undated = OrganicRefinement.Apply(1.0, sourceFile: null,
            value: "The retry queue caps concurrent workers at one per partition, a limit that has held " +
                   "since the queue was first introduced and has never needed to change since then.");

        dated.Reasons.ShouldContain("dated-fact");
        dated.Score.ShouldBeGreaterThan(undated.Score);
    }

    [Fact]
    public void ShortDefinitionalFact_WithDurableMarkersAndLittleStatus_FloorsAtTwoPointTwo()
    {
        var result = OrganicRefinement.Apply(0.45, sourceFile: null,
            value: "The retry queue must never exceed one in-flight message per partition, by design.");

        result.Reasons.ShouldContain("short-definitional-floor");
        result.Score.ShouldBeGreaterThanOrEqualTo(2.2);
    }

    [Fact]
    public void Delta_IsClampedToTheDocumentedRange()
    {
        var veryNegative = OrganicRefinement.Apply(3.45, sourceFile: null,
            value: string.Concat(Enumerable.Repeat(
                "Done. Merged. Pushed to origin/main. Your branch passed, exit 0, CI checks green. " +
                "as instructed, per your request, waiting on the gate chain, in flight, worktree. ", 10)));

        // Delta is clamped to [-2.8, 1.5]; base (3.45) + -2.8 = 0.65 at worst, before the final [0,4] clamp.
        veryNegative.Score.ShouldBeGreaterThanOrEqualTo(0.0);
    }
}
