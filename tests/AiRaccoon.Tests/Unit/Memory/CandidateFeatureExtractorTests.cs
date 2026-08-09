using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Foreign-project detection must recognize known alias spellings, not just the live
/// project id's exact string (docs/work/promotion-scoring-eval/round2/agentC/scorer.py PROJECT_ALIASES).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CandidateFeatureExtractorTests
{
    private static readonly string[] AllProjects = ["ai-badger", "ai-raccoon"];

    [Fact]
    public void Extract_AliasSpellingOfAnotherProject_CountsAsForeignSubject()
    {
        // "airaccoon" (no hyphen) is a known alias of "ai-raccoon" but not a substring of it.
        var features = CandidateFeatureExtractor.Extract(
            "the airaccoon deployment holds one bank per install", "ai-badger", AllProjects);

        features.ForeignSubject.ShouldBeTrue();
        features.ForeignProjects.ShouldBe(1);
    }

    [Fact]
    public void Extract_BareProjectIdMention_StillCountsAsForeignSubject()
    {
        var features = CandidateFeatureExtractor.Extract(
            "ai-raccoon's queue holds the propose tier", "ai-badger", AllProjects);

        features.ForeignSubject.ShouldBeTrue();
    }

    [Fact]
    public void Extract_UnaliasedProjectId_StillMatchesOnItsBareId()
    {
        var features = CandidateFeatureExtractor.Extract(
            "the acme pipeline runs nightly", "ai-badger", ["ai-badger", "acme"]);

        features.ForeignSubject.ShouldBeTrue();
    }

    /// <summary>Verified live false positive: the promotion queue's top-scored ai-raccoon candidate
    /// tripped foreign-subject purely on a parenthetical enumeration of other project ids.</summary>
    [Fact]
    public void ForeignProjectIdInAParenthetical_IsNotTheSubject()
    {
        var features = CandidateFeatureExtractor.Extract(
            "[facts] AiRaccoon deployment (2026-08-06): one bank per install — ~/.ai-raccoon/memory.db " +
            "holds all projects (project ids: ai-raccoon, job-search-ai-assistant, ai-badger).",
            "ai-raccoon", ["ai-raccoon", "ai-badger", "jsaa"]);

        features.ForeignSubject.ShouldBeFalse();
        features.ForeignProjects.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void ForeignProjectIdInProse_IsStillTheSubject()
    {
        // A bracketed span appears before the mention, guarding against over-stripping: only the
        // bracketed content itself should be removed, not the prose id that follows it.
        var features = CandidateFeatureExtractor.Extract(
            "(2026-08-06) ai-badger's scaffolding step rewrote the delegation map for every agent in " +
            "the repo without any manual edits required from the team reviewing the change.",
            "ai-raccoon", ["ai-raccoon", "ai-badger"]);

        features.ForeignSubject.ShouldBeTrue();
    }
}
