using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SharedExtractionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] AllProjects = ["ai-raccoon", "job-search-ai-assistant", "ai-badger"];

    private readonly SharedExtractionService _service = new();

    private static ExtractionCandidateRow Row(
        string hash, string? sourceFile = null, string value = "some content", int accessCount = 0,
        double rating = 0.5, DateTimeOffset? createdAt = null, int? ttlDays = null) =>
        new(hash, $"{hash}.md", value, sourceFile, rating, accessCount, createdAt ?? Now.AddDays(-5),
            ttlDays);

    [Fact]
    public void Propose_OrganicWrite_RanksFirst_WithOrganicNoteArchetype()
    {
        var rows = new[]
        {
            Row("a", sourceFile: "docs/x.md"),
            Row("b", sourceFile: null, value: "agent-written fact", accessCount: 0)
        };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Hash.ShouldBe("b");
        result.Candidates[0].Reasons.ShouldContain("organic-note");
        result.PromotedHashes.ShouldBeEmpty();
    }

    /// <summary>v2 narrows the incumbent's bare-substring `+2 cross-project` (which fired on 61/61
    /// candidates in the eval — see docs/adr/0018-promotion-scoring-v2.md) to a proximity-gated
    /// "foreign-subject" bonus: the mention must be near the opening of the chunk.</summary>
    [Fact]
    public void Propose_ForeignProjectMentionedNearTheOpening_AddsForeignSubjectSignal()
    {
        var rows = new[]
        {
            Row("a", sourceFile: "docs/x.md",
                value: "job-search-ai-assistant's pre-push gate rejects any commit missing a changelog " +
                       "entry, a convention this project adopted after tracing a release incident back " +
                       "to a missing entry that nobody noticed until the release notes came up short.")
        };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Reasons.ShouldContain("foreign-subject");
    }

    [Fact]
    public void Propose_OwnProjectIdInValue_IsNotForeignSubject()
    {
        var rows = new[] { Row("a", sourceFile: "docs/x.md", accessCount: 1, value: "the ai-raccoon pipeline ships facts") };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Reasons.ShouldNotContain("foreign-subject");
    }

    [Fact]
    public void Propose_UsageSignal_AddsAccessedReason()
    {
        var rows = new[]
        {
            Row("a", sourceFile: "docs/x.md", accessCount: 5, rating: 0.6,
                value: "Notes on the eviction policy behaviour under load, kept here for whoever revisits " +
                       "this file next time the cache size needs tuning for the current traffic pattern.")
        };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Reasons.ShouldContain("accessed");
    }

    /// <summary>A bare work-note chunk with no archetype boost and no content evidence sits below
    /// the v2 floor (docs/adr/0018-promotion-scoring-v2.md) — unlike v1, this has nothing to do with
    /// recency, which no longer feeds the score at all.</summary>
    [Fact]
    public void Propose_BareWorkNoteChunk_IsBelowTheFloor()
    {
        var rows = new[] { Row("a", sourceFile: "docs/x.md") };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void Propose_TtlRow_ExcludedByDefault_IncludedWhenFlagged()
    {
        var rows = new[] { Row("a", sourceFile: null, value: "ephemeral note", ttlDays: 30) };

        var excluded = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);
        excluded.Candidates.ShouldBeEmpty();

        var included = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], true, 20, Now);
        included.Candidates.ShouldHaveSingleItem();
        included.Candidates[0].Reasons.ShouldContain("ttl-row");
    }

    [Fact]
    public void Propose_LimitApplied_ByRankingScore()
    {
        var rows = new[]
        {
            // organic + rule language + foreign-subject.
            Row("high", sourceFile: null,
                value: "The retry queue must never drop a message silently; this is a hard invariant " +
                       "recorded here so nobody has to relearn it after job-search-ai-assistant hit the " +
                       "same failure mode independently last quarter and traced it to the same root cause."),
            // organic, plain prose, no bonuses.
            Row("mid", sourceFile: null,
                value: "The retry queue clears completed messages once every consumer has acknowledged " +
                       "them, keeping memory bounded during long backlogs without any operator " +
                       "intervention at all during normal day to day operation in every environment."),
            Row("low", sourceFile: "docs/x.md", accessCount: 1)
        };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 2, Now);

        result.Candidates.Count.ShouldBe(2);
        result.Candidates[0].Hash.ShouldBe("high");
        result.Candidates[1].Hash.ShouldBe("mid");
    }

    /// <summary>Recency is a sort tie-break only (never part of the score): two candidates that score
    /// identically rank by createdAt, most recent first.</summary>
    [Fact]
    public void Propose_TiedScores_BreakByRecency()
    {
        var rows = new[]
        {
            Row("older", sourceFile: null, value: "agent-written fact", createdAt: Now.AddDays(-10)),
            Row("newer", sourceFile: null, value: "agent-written fact", createdAt: Now.AddDays(-1))
        };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates[0].Score.ShouldBe(result.Candidates[1].Score);
        result.Candidates[0].Hash.ShouldBe("newer");
        result.Candidates[1].Hash.ShouldBe("older");
    }

    [Fact]
    public void Propose_AlreadySharedValue_IsDeduplicated()
    {
        var rows = new[] { Row("a", sourceFile: null, value: "  already  shared fact ") };
        var sharedValues = new HashSet<string> { "alreadysharedfact" }; // whitespace-normalized

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            sharedValues, [], false, 20, Now);

        result.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void Propose_SharedPath_IsDeduplicated()
    {
        var rows = new[] { Row("a", sourceFile: null, value: "fact value") };
        var sharedPaths = new HashSet<string> { "shared/a.md" };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], sharedPaths, false, 20, Now);

        result.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void Promote_ReturnsRankedHashes_ExcludingDeduplicated()
    {
        var rows = new[]
        {
            Row("dup", sourceFile: null, value: "already there"),
            Row("keep", sourceFile: null, value: "fresh organic fact"),
            Row("low", sourceFile: "docs/x.md", value: "Notes on the eviction policy.")
        };
        var sharedValues = new HashSet<string> { "alreadythere" };

        var result = _service.Run(ExtractMode.Promote, "ai-raccoon", AllProjects, rows,
            sharedValues, [], false, 20, Now);

        result.Candidates.Count.ShouldBe(2);
        result.Candidates[0].Hash.ShouldBe("keep");
        result.Candidates[1].Hash.ShouldBe("low");
        result.PromotedHashes.ShouldBe(["keep", "low"]);
    }

    [Fact]
    public void Run_EmptyRows_ReturnsEmptyResult()
    {
        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, [],
            [], [], false, 20, Now);

        result.Candidates.ShouldBeEmpty();
        result.PromotedHashes.ShouldBeEmpty();
    }

    [Fact]
    public void Propose_ValuePreview_IsTruncated()
    {
        var longValue = new string('x', 500);
        var rows = new[] { Row("a", sourceFile: null, value: longValue) };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].ValuePreview.Length.ShouldBeLessThanOrEqualTo(300);
    }

    /// <summary>v2 drops the incumbent's `+0.5 recent` additive bonus entirely (docs/adr/0018-promotion-scoring-v2.md):
    /// a re-propose of the same unchanged row after the old recency window must score identically —
    /// `now` no longer feeds the score at all, only the tie-break sort.</summary>
    [Fact]
    public void Propose_AfterTheOldRecencyWindow_ScoresTheSameRowUnchanged()
    {
        var row = Row("b", sourceFile: null, value: "agent-written fact", createdAt: Now);

        var fresh = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, [row], [], [], false, 20, Now);
        var aged = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, [row], [], [], false, 20,
            Now.AddDays(31));

        fresh.Candidates.ShouldHaveSingleItem();
        aged.Candidates.ShouldHaveSingleItem();
        aged.Candidates[0].Score.ShouldBe(fresh.Candidates[0].Score);
    }
}
