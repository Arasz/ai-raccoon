using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     Ports agentC/scorer.py's organic_adjust() and the organic-note branch of score_candidate()
///     (docs/adr/0018-promotion-scoring-v2.md, v3 section).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OrganicRefinementTests
{
    private static readonly string[] AllProjects = ["proj-alpha", "proj-beta"];

    private static OrganicRefinementResult Apply(double baseScore, string value, string projectId = "proj-alpha") =>
        OrganicRefinement.Apply(baseScore, value, projectId, AllProjects);

    [Fact]
    public void StatusOpener_InFirst80Chars_IsPenalized()
    {
        var statusDump = Apply(2.30,
            "Done. Everything shipped to main, the full suite passed and CI is green across the board " +
            "with nothing left in flight for this rollout, and the on-call handoff notes are current.");
        var plainFact = Apply(2.30,
            "The cache invalidation queue clears entries once their TTL expires, keeping memory " +
            "pressure predictable across long-running sessions without any operator input at all.");

        statusDump.Reasons.ShouldContain("status-opener");
        statusDump.Score.ShouldBeLessThan(plainFact.Score);
    }

    /// <summary>The opener check strips leading markdown decoration before taking the 80-char head.</summary>
    [Fact]
    public void StatusOpener_BehindMarkdownDecoration_IsStillDetected()
    {
        var decorated = Apply(2.30,
            "** - Done. Everything shipped to main, the full suite passed and CI is green across the " +
            "board with nothing left in flight for this rollout, and the handoff notes are current.");

        decorated.Reasons.ShouldContain("status-opener");
    }

    /// <summary>A generic "<X> complete/done/closed/finished/delivered" opener is recognized,
    /// not just the earlier enumerated literal openers.</summary>
    [Fact]
    public void GenericCompleteOpener_IsRecognized()
    {
        var generic = Apply(2.30,
            "Extension rollout finished: every downstream consumer now reads from the new endpoint and " +
            "the old one is scheduled for removal once the last dependent service migrates over fully.");

        generic.Reasons.ShouldContain("status-opener");
    }

    [Fact]
    public void StatusVocabularyDensity_IsPenalized()
    {
        var statusHeavy = Apply(2.30,
            "The branch was merged and pushed to origin/main; the pre-push gate chain and the full " +
            "suite passed, CI checks are green, and the work is handed over ready for review.");

        statusHeavy.Reasons.ShouldContain("status-vocabulary");
    }

    [Fact]
    public void SecondPersonAddress_IsPenalized()
    {
        var addressed = Apply(2.30,
            "As instructed, your branch is ready and you can merge it whenever the review finishes " +
            "since every check has already gone green on your behalf across the whole pipeline.");

        addressed.Reasons.ShouldContain("second-person");
    }

    [Fact]
    public void TwoOrMoreCommitHashes_ArePenalized()
    {
        var withHashes = Apply(2.30,
            "Cherry-picked a1b2c3d onto the release branch after rebasing past 9f8e7d6, then verified " +
            "the working tree matched the expected state before handing the branch back over.");

        withHashes.Reasons.ShouldContain("commit-hashes");
    }

    [Fact]
    public void TestCountNumbers_AreExcludedFromMeasuredEvidence()
    {
        var testCountsOnly = Apply(0.45,
            "Ran the full suite: 174 passed, 0 failed, 0 skipped, exit 0. All checks are green and " +
            "the run is reproducible across machines whenever CI happens to re-trigger the job.");
        var realMeasurement = Apply(0.45,
            "Wall time dropped from 640ms to 210ms after the change, a measured 3x improvement that " +
            "held steady across five separate machines when the benchmark was re-run each time.");

        testCountsOnly.Reasons.ShouldNotContain("real-measurements");
        realMeasurement.Reasons.ShouldContain("real-measurements");
    }

    [Fact]
    public void DurableFactLanguage_IsRewarded()
    {
        var durable = Apply(1.0,
            "[facts] The retry queue must never process more than one message per worker at a time; " +
            "this is a contract the dispatcher relies on and its precedence over batching is by design.");

        durable.Reasons.ShouldContain("durable-fact-language");
    }

    [Fact]
    public void DatedFactFraming_NearTheStart_IsRewarded()
    {
        var dated = Apply(1.0,
            "(2026-05-01): the retry queue caps concurrent workers at one per partition, a limit " +
            "that has held since the queue was first introduced and has never needed to change.");
        var undated = Apply(1.0,
            "The retry queue caps concurrent workers at one per partition, a limit that has held " +
            "since the queue was first introduced and has never needed to change since then.");

        dated.Reasons.ShouldContain("dated-fact");
        dated.Score.ShouldBeGreaterThan(undated.Score);
    }

    [Fact]
    public void ForeignProjectSubject_IsRewarded()
    {
        var withoutForeign = Apply(1.0,
            "the retry queue caps concurrent workers at one per partition, a limit that has " +
            "held since it was introduced and has never needed to change across any release since.",
            projectId: "proj-alpha");
        var withForeign = Apply(1.0,
            "proj-beta's retry queue caps concurrent workers at one per partition, a limit that has " +
            "held since it was introduced and has never needed to change across any release since.",
            projectId: "proj-alpha");

        withForeign.Reasons.ShouldContain("foreign-subject");
        withoutForeign.Reasons.ShouldNotContain("foreign-subject");
        withForeign.Score.ShouldBe(withoutForeign.Score + 0.20, 0.0001);
    }

    /// <summary>The mid-sentence penalty is doc-channel-only by decision (pending calibration
    /// fixtures): OrganicRefinement.Apply must never reference CandidateFeatures.MidSentence.</summary>
    [Fact]
    public void MidSentenceOpener_DoesNotChangeAnOrganicNoteScore()
    {
        var baseline = new CandidateFeatures(
            RuleDensity: 0, MeasureWords: 0, NumUnit: 0, Ephemera: 0, Superseded: false,
            FindingRows: 0, TableFrac: 0, LinkDensity: 0, DocnameDensity: 0, VersionRows: 0,
            Frontmatter: false, NChars: 500, NWords: 80, MidSentence: false, HeadingStart: false,
            ForeignProjects: 0, ForeignSubject: false, StatusOpener: false, StatusVocab: 0,
            SecondPerson: false, CommitHashes: 0, RealMeasures: 0, DurableLoose: 1, DatedFact: false,
            FirstPerson: 0, MetaHeader: 0, Imperatives: 0, Urls: 0, ContentsIndex: false, DirReadme: false);

        var withoutMidSentence = OrganicRefinement.Apply(baseline, 2.30);
        var withMidSentence = OrganicRefinement.Apply(baseline with { MidSentence = true }, 2.30);

        withMidSentence.Score.ShouldBe(withoutMidSentence.Score);
        withMidSentence.Reasons.ShouldNotContain("mid-sentence");
    }

    [Fact]
    public void PointerShapedOrganicWrite_IsPenalized()
    {
        var pointerHeavy = Apply(2.30,
            "See [a](docs/a.md), [b](docs/b.md) and [c](docs/c.md) for the full eviction policy " +
            "write-up; the summary here is intentionally short and just names the three documents.");

        pointerHeavy.Reasons.ShouldContain("pointer-density");
    }

    [Fact]
    public void ContentsIndexHeading_IsPenalized()
    {
        var contents = Apply(2.30,
            "## Contents\nThis chunk is a table of contents heading for a longer document, not a " +
            "standalone fact, and it should not be promoted as if it stated something durable at all.");

        contents.Reasons.ShouldContain("contents-index");
    }

    [Fact]
    public void MetadataHeaderBlock_IsPenalized()
    {
        var metaHeavy = Apply(2.30,
            "**Task:** cache sweep\nThe sweep compares five eviction policies against the same replay " +
            "trace and records the hit rate and CPU cost of each one across the full run for review.");

        metaHeavy.Reasons.ShouldContain("metadata-header");
    }

    [Fact]
    public void DirectoryReadmeHeading_IsPenalized()
    {
        var dirReadme = Apply(2.30,
            "# cache/\nThis directory holds the eviction policy implementations and their accompanying " +
            "unit tests, organized one file per policy so each can be reviewed independently over time.");

        dirReadme.Reasons.ShouldContain("directory-readme");
    }

    [Fact]
    public void SupersededMarkers_ArePenalized()
    {
        var superseded = Apply(2.30,
            "This guidance was superseded by the 2026-05 sweep; the historical note below no longer " +
            "applies to the current cache layer and is kept only for archival context going forward.");

        superseded.Reasons.ShouldContain("superseded");
    }

    [Fact]
    public void ShortDefinitionalFact_WithDurableMarkersAndLittleStatus_FloorsAtTwoPointFour()
    {
        var result = Apply(0.45, "The retry queue must never exceed one in-flight message per partition, by design.");

        result.Reasons.ShouldContain("short-definitional-floor");
        result.Score.ShouldBeGreaterThanOrEqualTo(2.4);
    }

    [Fact]
    public void Delta_IsClampedToTheDocumentedRange()
    {
        var veryNegative = Apply(2.30, string.Concat(Enumerable.Repeat(
            "Done. Merged. Pushed to origin/main. Your branch passed, exit 0, CI checks green. " +
            "as instructed, per your request, waiting on the gate chain, in flight, worktree. ", 10)));

        // Delta clamps to [-2.2, 1.6]; base (2.30) + -2.2 = 0.10 at worst, before the final [0,4] clamp.
        veryNegative.Score.ShouldBeGreaterThanOrEqualTo(0.0);
    }
}
