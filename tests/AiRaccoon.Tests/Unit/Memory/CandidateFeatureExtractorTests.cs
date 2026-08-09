using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Foreign-project detection must recognize known alias spellings, not just the live
/// project id's exact string (scorer.py's PROJECT_ALIASES), and the portability features (tech
/// breadth, cross-reference density) that scorer.py's round-3 lane-A features() adds.</summary>
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

    [Fact]
    public void Extract_ForeignIdOutsideTheFirst250Chars_DoesNotCountAsSubject()
    {
        var padding = string.Concat(Enumerable.Repeat(
            "The release train keeps a changelog entry for every commit so history stays reviewable. ", 6));

        var features = CandidateFeatureExtractor.Extract(
            padding + "A practice later adopted by ai-raccoon as well, long after this paragraph opened.",
            "ai-badger", AllProjects);

        features.ForeignSubject.ShouldBeFalse();
    }

    /// <summary>Named third-party technology (sqlite, dotnet, github actions, ...) is the portability
    /// signal (scorer.py's TECH_RE); breadth counts distinct matched vocabulary, not raw hits.</summary>
    [Fact]
    public void Extract_DistinctNamedTechnologies_CountTowardTechBreadth()
    {
        var features = CandidateFeatureExtractor.Extract(
            "The Aspire AppHost talks to sqlite over WAL mode and the pipeline runs on GitHub Actions " +
            "with dotnet build as the first step of every workflow that touches this repository.",
            "ai-raccoon", AllProjects);

        features.TechBreadth.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void Extract_RepeatedMentionOfTheSameTechnology_CountsOnceForBreadth()
    {
        var features = CandidateFeatureExtractor.Extract(
            "sqlite sqlite sqlite sqlite sqlite sqlite sqlite sqlite sqlite sqlite sqlite sqlite",
            "ai-raccoon", AllProjects);

        features.TechBreadth.ShouldBe(1);
    }

    [Fact]
    public void Extract_PlainProseWithNoNamedTechnology_HasZeroTechBreadth()
    {
        var features = CandidateFeatureExtractor.Extract(
            "The scheduling component tracks each background job by its identifier and records the " +
            "queue it belongs to along with the time it entered that queue for later processing.",
            "ai-raccoon", AllProjects);

        features.TechBreadth.ShouldBe(0);
    }

    /// <summary>Intra-repo bookkeeping (ADR-nn, issue #nn, section marks) is the mirror-image signal
    /// (scorer.py's XREF_RE) — dense cross-referencing is the opposite of portability.</summary>
    [Fact]
    public void Extract_IntraRepoCrossReferences_RaiseXrefDensity()
    {
        var withXrefs = CandidateFeatureExtractor.Extract(
            "See ADR-12 §3 and issue #482 for the full rationale behind this decision and its NFR-A scope.",
            "ai-raccoon", AllProjects);
        var withoutXrefs = CandidateFeatureExtractor.Extract(
            "See the linked design document for the full rationale behind this decision and its scope.",
            "ai-raccoon", AllProjects);

        withXrefs.XrefDensity.ShouldBeGreaterThan(withoutXrefs.XrefDensity);
    }

    /// <summary>An impersonal "is/are/means/requires/holds/applies ... never/always/only/not" clause
    /// is the durability signal (scorer.py's IMPERSONAL_RULE_RE) distinguishing a rule from a report.</summary>
    [Fact]
    public void Extract_ImpersonalRuleClause_RaisesImpRuleDensity()
    {
        var withRule = CandidateFeatureExtractor.Extract(
            "The retry policy requires that a worker never exceed one in-flight message per partition.",
            "ai-raccoon", AllProjects);
        var withoutRule = CandidateFeatureExtractor.Extract(
            "The retry policy was updated last week after a worker exceeded its in-flight message budget.",
            "ai-raccoon", AllProjects);

        withRule.ImpRuleDensity.ShouldBeGreaterThan(withoutRule.ImpRuleDensity);
    }

    /// <summary>Round-3 lane-A drops the markdown-decoration stripping that used to guard mid-sentence
    /// detection: it now matches scorer.py's plain `v.lstrip()` exactly (whitespace only).</summary>
    [Fact]
    public void Extract_LeadingMarkdownEmphasis_IsNotStrippedBeforeMidSentenceCheck()
    {
        var features = CandidateFeatureExtractor.Extract(
            "**minscore is measured inert to whatever the caller passes as a filter, so it never trims.",
            "ai-raccoon", AllProjects);

        // First non-whitespace char is '*', which is neither lowercase nor in "),;" — not mid-sentence.
        features.MidSentence.ShouldBeFalse();
    }

    [Fact]
    public void Extract_LowercaseOpener_IsMidSentence()
    {
        var features = CandidateFeatureExtractor.Extract(
            "clears entries older than the configured TTL once the queue notices them, which keeps " +
            "memory pressure predictable across long-running sessions without any operator input.",
            "ai-raccoon", AllProjects);

        features.MidSentence.ShouldBeTrue();
    }

    /// <summary>Round-3 lane-A restores the heading-start bonus dropped for exact-parity reasons:
    /// measurement showed restoring it beats the parity port on train, holdout and the owner guard
    /// (scorer.py's `v.lstrip().startswith("#")`).</summary>
    [Fact]
    public void Extract_DocumentStartingWithAHeading_SetsHeadingStart()
    {
        var withHeading = CandidateFeatureExtractor.Extract(
            "# Cache eviction policy\n\nEntries expire once their TTL lapses under the current design.",
            "ai-raccoon", AllProjects);
        var withoutHeading = CandidateFeatureExtractor.Extract(
            "Entries expire once their TTL lapses under the current cache eviction design.",
            "ai-raccoon", AllProjects);

        withHeading.HeadingStart.ShouldBeTrue();
        withoutHeading.HeadingStart.ShouldBeFalse();
    }

    [Fact]
    public void Extract_LeadingWhitespaceBeforeHeadingMarker_StillSetsHeadingStart()
    {
        var features = CandidateFeatureExtractor.Extract(
            "\n\n  ## Retry policy\n\nWorkers back off exponentially between attempts on failure.",
            "ai-raccoon", AllProjects);

        features.HeadingStart.ShouldBeTrue();
    }
}
