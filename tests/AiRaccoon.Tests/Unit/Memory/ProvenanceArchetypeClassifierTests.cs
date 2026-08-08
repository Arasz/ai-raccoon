using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ports agentB/scorer.py's archetype() ordering (see docs/adr/0018-promotion-scoring-v2.md).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProvenanceArchetypeClassifierTests
{
    [Fact]
    public void OrganicWrite_NullSourceFile_IsOrganicNote()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify("a1b2c3.md", sourceFile: null, value: "a fact");

        archetype.ShouldBe(ProvenanceArchetype.OrganicNote);
    }

    [Fact]
    public void ContentAddressedHexFilename_IsOrganicNote_EvenWithSourceFile()
    {
        var hex = new string('a', 40) + ".md";

        var archetype = ProvenanceArchetypeClassifier.Classify(hex, sourceFile: "somewhere.md", value: "a fact");

        archetype.ShouldBe(ProvenanceArchetype.OrganicNote);
    }

    [Fact]
    public void AdrPath_IsAdr()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/adr/0021-cache-invalidation.md", sourceFile: "docs/adr/0021-cache-invalidation.md",
            value: "a decision");

        archetype.ShouldBe(ProvenanceArchetype.Adr);
    }

    [Fact]
    public void NumberedFilename_NotDated_IsAdr()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/0021-cache-invalidation.md", sourceFile: "docs/0021-cache-invalidation.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Adr);
    }

    /// <summary>The defect the eval harness caught: a dated work note (`YYYY-MM-DD-...`) must
    /// NOT match the ADR's `^\d{4}-` pattern.</summary>
    [Fact]
    public void DatedWorkNote_IsNotAdr()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/2026-05-02-cache-notes.md", sourceFile: "docs/work/2026-05-02-cache-notes.md", value: "x");

        archetype.ShouldNotBe(ProvenanceArchetype.Adr);
        archetype.ShouldBe(ProvenanceArchetype.WorkNote);
    }

    [Fact]
    public void ReadmeBasename_IsDocIndex()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/changelog/README.md", sourceFile: "docs/changelog/README.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.DocIndex);
    }

    [Fact]
    public void CharterInFilename_IsCharter()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/2026-05-01-review-charter.md", sourceFile: "docs/work/2026-05-01-review-charter.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Charter);
    }

    [Fact]
    public void ExplanationPath_IsExplanation()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/explanation/queue-architecture.md", sourceFile: "docs/explanation/queue-architecture.md",
            value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Explanation);
    }

    [Fact]
    public void PlansPath_IsPlan()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/plans/2026-05-01-cache-plan.md", sourceFile: "docs/plans/2026-05-01-cache-plan.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Plan);
    }

    [Fact]
    public void ReviewsPath_IsReview()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/reviews/2026-04-01-perf-review.md", sourceFile: "docs/work/reviews/2026-04-01-perf-review.md",
            value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Review);
    }

    [Fact]
    public void SweepInFilename_IsMeasurement()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/cache-benchmark-results.md", sourceFile: "docs/work/cache-benchmark-results.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Measurement);
    }

    [Fact]
    public void ArchiveResearch_IsResearchSynthesis()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/archive/2026-03-01-research-synthesis.md",
            sourceFile: "docs/work/archive/2026-03-01-research-synthesis.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.ResearchSynthesis);
    }

    [Fact]
    public void ReferencePath_IsReference()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/reference/cli-flags.md", sourceFile: "docs/reference/cli-flags.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Reference);
    }

    [Fact]
    public void SkillsMdBasename_IsCatalogPage()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/skills.md", sourceFile: "docs/skills.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.CatalogPage);
    }

    [Fact]
    public void ChangelogPath_NonIndexBasename_IsChangelogEntry()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/changelog/0.104.0-widget-release-notes.md",
            sourceFile: "docs/changelog/0.104.0-widget-release-notes.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.ChangelogEntry);
    }

    [Fact]
    public void UnrecognisedPath_FallsBackToWorkNote()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/misc/random-topic.md", sourceFile: "docs/misc/random-topic.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.WorkNote);
    }

    [Fact]
    public void TwoOrMoreInvokeMarkupHits_IsTurnMirror_EvenWithNullSourceFile()
    {
        var value = "<invoke name=\"Bash\">...</invoke><invoke name=\"Read\">...</invoke>";

        var archetype = ProvenanceArchetypeClassifier.Classify("session.md", sourceFile: null, value: value);

        archetype.ShouldBe(ProvenanceArchetype.TurnMirror);
    }

    [Fact]
    public void Prior_MatchesTheEvalReport()
    {
        var expected = new Dictionary<ProvenanceArchetype, double>
        {
            [ProvenanceArchetype.OrganicNote] = 3.45,
            [ProvenanceArchetype.Adr] = 3.00,
            [ProvenanceArchetype.Charter] = 2.45,
            [ProvenanceArchetype.Explanation] = 2.30,
            [ProvenanceArchetype.Measurement] = 2.10,
            [ProvenanceArchetype.ResearchSynthesis] = 1.90,
            [ProvenanceArchetype.Reference] = 1.45,
            [ProvenanceArchetype.WorkNote] = 1.15,
            [ProvenanceArchetype.CatalogPage] = 1.10,
            [ProvenanceArchetype.ChangelogEntry] = 1.05,
            [ProvenanceArchetype.Plan] = 0.85,
            [ProvenanceArchetype.Review] = 0.80,
            [ProvenanceArchetype.DocIndex] = 0.25,
            [ProvenanceArchetype.TurnMirror] = 0.45
        };

        foreach (var (archetype, prior) in expected)
        {
            ProvenanceArchetypeClassifier.Prior(archetype).ShouldBe(prior, archetype.ToString());
        }
    }
}
