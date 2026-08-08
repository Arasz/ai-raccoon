using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ports agentC/scorer.py's channel() ordering; see docs/adr/0018-promotion-scoring-v2.md.</summary>
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
    public void RememberDirectoryPath_IsRememberLog_EvenBeforeTheOrganicCheck()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            ".remember/2026-08-01-status.md", sourceFile: ".remember/2026-08-01-status.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.RememberLog);
    }

    [Fact]
    public void ClaudeAutoMemory_MemoryMdBasename_IsAutoMemoryIndex()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "/Users/dev/.claude/projects/x/memory/MEMORY.md",
            sourceFile: "/Users/dev/.claude/projects/x/memory/MEMORY.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.AutoMemoryIndex);
    }

    [Theory]
    [InlineData("session-2026-08-01.md")]
    [InlineData("status-handoff.md")]
    [InlineData("handoff-notes.md")]
    public void ClaudeAutoMemory_SessionStatusOrHandoffBasename_IsAutoMemorySession(string basename)
    {
        var path = $"/Users/dev/.claude/projects/x/memory/{basename}";

        var archetype = ProvenanceArchetypeClassifier.Classify(path, sourceFile: path, value: "x");

        archetype.ShouldBe(ProvenanceArchetype.AutoMemorySession);
    }

    [Fact]
    public void ClaudeAutoMemory_NamedBasename_IsAutoMemoryNote()
    {
        var path = "/Users/dev/.claude/projects/x/memory/nuget-lock-starvation-not-node-reuse.md";

        var archetype = ProvenanceArchetypeClassifier.Classify(path, sourceFile: path, value: "x");

        archetype.ShouldBe(ProvenanceArchetype.AutoMemoryNote);
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

    /// <summary>A dated work note (`YYYY-MM-DD-...`) must not match the ADR's `^\d{4}-` pattern.</summary>
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
    public void UndatedCharterInFilename_IsCharter()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/charter.md", sourceFile: "docs/charter.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Charter);
    }

    /// <summary>v3 change: a dated `YYYY-MM-DD-*-charter.md` under docs/work is in-flight review
    /// coordination, not a durable project charter (docs/adr/0018-promotion-scoring-v2.md).</summary>
    [Fact]
    public void DatedCharterInFilename_IsReview_NotCharter()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/2026-05-01-review-charter.md", sourceFile: "docs/work/2026-05-01-review-charter.md",
            value: "x");

        archetype.ShouldBe(ProvenanceArchetype.Review);
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

    /// <summary>`/docs/work/` stays work-note; other `/docs/` paths route to other-doc instead of
    /// work-note. Ported bug-for-bug from the prototype: the check is a literal `/docs/`
    /// substring, so only an absolute path (real ingest source_file shape) matches.</summary>
    [Fact]
    public void UnrecognisedDocsPath_NotUnderWork_IsOtherDoc()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "notes/docs/misc/random-topic.md", sourceFile: "notes/docs/misc/random-topic.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.OtherDoc);
    }

    [Fact]
    public void UnrecognisedPath_OutsideDocs_FallsBackToWorkNote()
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "misc/random-topic.md", sourceFile: "misc/random-topic.md", value: "x");

        archetype.ShouldBe(ProvenanceArchetype.WorkNote);
    }

    [Fact]
    public void TwoOrMoreInvokeMarkupHits_NearTheStart_IsTurnMirror_EvenWithNullSourceFile()
    {
        var value = "<invoke name=\"Bash\">...</invoke><invoke name=\"Read\">...</invoke>";

        var archetype = ProvenanceArchetypeClassifier.Classify("session.md", sourceFile: null, value: value);

        archetype.ShouldBe(ProvenanceArchetype.TurnMirror);
    }

    /// <summary>Prose-prefix rescue: a transcript starting 300+ chars in is not a turn-mirror — the
    /// candidate is classified (and later scored) on its path/prose, not the appended transcript.</summary>
    [Fact]
    public void InvokeMarkup_StartingLate_IsNotTurnMirror()
    {
        var prose = string.Concat(Enumerable.Repeat("This paragraph exists only to push the transcript well " +
                                                      "past the rescue threshold before anything tool-shaped " +
                                                      "appears in the text at all. ", 4));
        var value = prose + "<invoke name=\"Bash\">...</invoke><invoke name=\"Read\">...</invoke>";

        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/notes.md", sourceFile: "docs/work/notes.md", value: value);

        archetype.ShouldNotBe(ProvenanceArchetype.TurnMirror);
    }

    [Fact]
    public void Prior_MatchesTheEvalReport()
    {
        var expected = new Dictionary<ProvenanceArchetype, double>
        {
            [ProvenanceArchetype.TurnMirror] = 0.35,
            [ProvenanceArchetype.RememberLog] = 0.30,
            [ProvenanceArchetype.AutoMemorySession] = 0.30,
            [ProvenanceArchetype.AutoMemoryIndex] = 0.55,
            [ProvenanceArchetype.AutoMemoryNote] = 2.70,
            [ProvenanceArchetype.OrganicNote] = 2.30,
            [ProvenanceArchetype.DocIndex] = 0.35,
            [ProvenanceArchetype.Adr] = 2.55,
            [ProvenanceArchetype.Charter] = 2.30,
            [ProvenanceArchetype.Explanation] = 2.15,
            [ProvenanceArchetype.Measurement] = 2.10,
            [ProvenanceArchetype.ResearchSynthesis] = 1.75,
            [ProvenanceArchetype.Reference] = 1.45,
            [ProvenanceArchetype.ChangelogEntry] = 1.05,
            [ProvenanceArchetype.WorkNote] = 1.30,
            [ProvenanceArchetype.Plan] = 0.70,
            [ProvenanceArchetype.Review] = 0.95,
            [ProvenanceArchetype.CatalogPage] = 1.05,
            [ProvenanceArchetype.OtherDoc] = 1.10
        };

        foreach (var (archetype, prior) in expected)
        {
            ProvenanceArchetypeClassifier.Prior(archetype).ShouldBe(prior, archetype.ToString());
        }
    }
}
