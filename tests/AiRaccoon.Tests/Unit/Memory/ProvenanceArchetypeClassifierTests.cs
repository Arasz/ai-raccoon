using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ports scorer.py's channel() ordering; see docs/adr/0018-promotion-scoring-v2.md
/// round-3 lane-A section.</summary>
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

    /// <summary>The organic check (a hex `path`) must win even when `source_file` happens to look
    /// like a document-channel path — on an organic write, `source_file` is a citation, not a
    /// provenance (METHOD.md §7). This is the corrected rule order: is_organic before any
    /// document-path-shaped routing, not after.</summary>
    [Fact]
    public void HexPathWithDocumentShapedSourceFile_IsStillOrganicNote_NotTheCitedChannel()
    {
        var hex = new string('a', 40) + ".md";

        var archetype = ProvenanceArchetypeClassifier.Classify(
            hex, sourceFile: ".remember/2026-08-01-status.md", value: "a fact");

        archetype.ShouldBe(ProvenanceArchetype.OrganicNote);
    }

    [Fact]
    public void RememberDirectoryPath_IsRememberLog()
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

    /// <summary>A dated `YYYY-MM-DD-*-charter.md` under docs/work is in-flight review coordination,
    /// not a durable project charter (docs/adr/0018-promotion-scoring-v2.md).</summary>
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

    /// <summary>A Hermes conversation id used as source_file routes to its own below-floor channel —
    /// a chat dump, whatever it looks like (METHOD.md §7) — checked before the mirror-adjacent
    /// turn-mirror check has a chance to fall through, and before the organic/document split.</summary>
    [Theory]
    [InlineData("hermes/20260806_215718_fd7f66")]
    [InlineData("some/prefix/hermes/20260806_215718_fd7f66")]
    [InlineData("hermes/84304448-08b6-4aec-a32b-af9f3a67097b")]
    public void HermesConversationIdSourceFile_IsTranscript(string sourceFile)
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/notes.md", sourceFile: sourceFile, value: "a conversation recap");

        archetype.ShouldBe(ProvenanceArchetype.Transcript);
    }

    /// <summary>A dated document that merely lives under a `hermes/` directory is not a conversation
    /// id: both live id shapes are a timestamp followed by `_`, or a UUID, and neither admits a
    /// dash-then-prose filename.</summary>
    [Theory]
    [InlineData("hermes/20260809-foo.md")]
    [InlineData("hermes/notes-plan.md")]
    public void DatedDocumentUnderAHermesDirectory_IsNotATranscript(string sourceFile)
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(
            "docs/work/notes.md", sourceFile: sourceFile, value: "a durable note about something");

        archetype.ShouldNotBe(ProvenanceArchetype.Transcript);
    }

    /// <summary>The transcript check reads `source_file` even on a hex (organic-write) path — a
    /// citation of "this came from a chat" outranks the usual organic routing (METHOD.md §7).</summary>
    [Fact]
    public void HermesConversationIdSourceFile_OutranksOrganicRouting_OnAHexPath()
    {
        var hex = new string('a', 40) + ".md";

        var archetype = ProvenanceArchetypeClassifier.Classify(
            hex, sourceFile: "hermes/20260806_215718_fd7f66", value: "a conversation recap");

        archetype.ShouldBe(ProvenanceArchetype.Transcript);
    }

    [Fact]
    public void Prior_MatchesTheEvalReport()
    {
        var expected = new Dictionary<ProvenanceArchetype, double>
        {
            [ProvenanceArchetype.Transcript] = 0.15,
            [ProvenanceArchetype.TurnMirror] = 0.25,
            [ProvenanceArchetype.RememberLog] = 0.30,
            [ProvenanceArchetype.AutoMemorySession] = 0.30,
            [ProvenanceArchetype.AutoMemoryIndex] = 0.35,
            [ProvenanceArchetype.DocIndex] = 0.30,
            [ProvenanceArchetype.AutoMemoryNote] = 2.06,
            [ProvenanceArchetype.OrganicNote] = 2.00,
            [ProvenanceArchetype.Adr] = 1.42,
            [ProvenanceArchetype.Charter] = 1.70,
            [ProvenanceArchetype.Explanation] = 1.61,
            [ProvenanceArchetype.Measurement] = 1.03,
            [ProvenanceArchetype.ResearchSynthesis] = 1.48,
            [ProvenanceArchetype.Reference] = 1.47,
            [ProvenanceArchetype.ChangelogEntry] = 1.37,
            [ProvenanceArchetype.WorkNote] = 1.44,
            [ProvenanceArchetype.Plan] = 1.20,
            [ProvenanceArchetype.Review] = 1.27,
            [ProvenanceArchetype.CatalogPage] = 1.26,
            [ProvenanceArchetype.OtherDoc] = 1.17
        };

        foreach (var (archetype, prior) in expected)
        {
            ProvenanceArchetypeClassifier.Prior(archetype).ShouldBe(prior, archetype.ToString());
        }
    }
}
