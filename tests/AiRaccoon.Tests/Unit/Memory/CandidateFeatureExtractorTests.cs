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
}
