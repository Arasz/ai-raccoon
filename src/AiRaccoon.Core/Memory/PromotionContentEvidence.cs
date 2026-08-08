using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory;

/// <summary>Bounded content-shape adjustment plus the plain-name reason tags that fired.</summary>
internal readonly record struct ContentEvidence(double Adjustment, IReadOnlyList<string> Reasons);

/// <summary>Bounded content-shape evidence that moves a candidate off its archetype prior (ported from
/// agentB/scorer.py's features()/score_candidate(), see docs/adr/0018-promotion-scoring-v2.md).</summary>
internal static partial class PromotionContentEvidence
{
    private const double Lo = -1.60;
    private const double PositiveHiDefault = 1.30;
    private const double PositiveHiCapped = 0.15;
    private const int ForeignSubjectHeadChars = 250;

    internal static ContentEvidence Evaluate(
        string value, ProvenanceArchetype archetype, string projectId, IReadOnlyList<string> allProjectIds,
        int accessCount)
    {
        var v = value ?? string.Empty;
        var wordCount = Words().Matches(v).Count;
        var lines = v.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var lineCount = Math.Max(lines.Count, 1);

        var reasons = new List<string>();
        var adj = 0.0;

        // Generalisable-rule language: the thing the shared tier exists to carry.
        var ruleDensity = Per100(RuleLanguage().Matches(v).Count, wordCount);
        var ruleBonus = Clamp(0.30 * ruleDensity, 0.0, 0.75);
        if (ruleBonus > 0)
        {
            adj += ruleBonus;
            reasons.Add("rule-language");
        }

        // A measurement is durable, but only when the words of measurement accompany the numbers.
        var measureWords = MeasureWords().Matches(v).Count;
        var numUnit = NumberWithUnit().Matches(v).Count;
        var measuredBonus = 0.0;
        if (measureWords >= 1 && numUnit >= 1)
        {
            measuredBonus = Clamp(0.12 * Math.Min(measureWords, 4) + 0.05 * Math.Min(numUnit, 4), 0.0, 0.50);
        }
        else if (measureWords >= 2)
        {
            measuredBonus = 0.15;
        }

        if (measuredBonus > 0)
        {
            adj += measuredBonus;
            reasons.Add("measured-values");
        }

        // Subject is another project (proximity-gated to the opening of the chunk, not a bare
        // substring anywhere in the text): the fact already reached across a boundary.
        var head = v.Length > ForeignSubjectHeadChars ? v[..ForeignSubjectHeadChars] : v;
        var foreignSubject = OtherProjectIds(projectId, allProjectIds).Any(
            id => head.Contains(id, StringComparison.OrdinalIgnoreCase));
        if (foreignSubject)
        {
            adj += 0.25;
            reasons.Add("foreign-subject");
        }

        var foreignProjectCount = OtherProjectIds(projectId, allProjectIds)
            .Count(id => v.Contains(id, StringComparison.OrdinalIgnoreCase));
        if (foreignProjectCount >= 2)
        {
            adj += 0.10;
            if (!foreignSubject)
            {
                reasons.Add("foreign-subject");
            }
        }

        var stripped = v.TrimStart();
        var firstChar = stripped.Length > 0 ? stripped[0] : '\0';
        var headingStart = stripped.StartsWith('#');
        var midSentence = char.IsLower(firstChar) || firstChar is ')' or ',' or ';';

        if (headingStart)
        {
            adj += 0.12;
            reasons.Add("heading-start");
        }

        if (midSentence)
        {
            adj -= 0.18;
            reasons.Add("mid-sentence");
        }

        // Weak, saturating recency-of-use signal.
        if (accessCount > 0)
        {
            adj += Clamp(0.05 * Math.Log(accessCount + 1), 0.0, 0.20);
            reasons.Add("accessed");
        }

        // Pointer rows: the payload names other documents instead of stating a fact.
        var tableRowCount = lines.Count(l => TableRow().IsMatch(l));
        var tableFraction = (double)tableRowCount / lineCount;
        var linkDensity = Per100(MarkdownLink().Matches(v).Count, wordCount);
        var docnameDensity = Per100(DocFilename().Matches(v).Count, wordCount);
        var versionRows = VersionRow().Matches(v).Count;

        var pointer = 0.0;
        if (tableFraction >= 0.55)
        {
            pointer += 0.30;
        }

        if (linkDensity >= 1.5)
        {
            pointer += 0.45;
        }

        if (docnameDensity >= 2.0)
        {
            pointer += 0.35;
        }

        if (versionRows >= 3)
        {
            pointer += 0.35;
        }

        pointer = Clamp(pointer, 0.0, 1.00);
        if (pointer > 0)
        {
            adj -= pointer;
            reasons.Add("pointer-density");
        }

        // Rows of a findings/work-package register.
        var findingRows = FindingRow().Matches(v).Count;
        if (findingRows >= 3)
        {
            adj -= 0.45;
            reasons.Add("finding-rows");
        }
        else if (findingRows >= 1)
        {
            adj -= 0.28;
            reasons.Add("finding-rows");
        }

        // In-flight coordination language.
        var ephemeraDensity = Per100(Ephemera().Matches(v).Count, wordCount);
        var ephemeraPenalty = Clamp(0.22 * ephemeraDensity, 0.0, 0.65);
        if (ephemeraPenalty > 0)
        {
            adj -= ephemeraPenalty;
            reasons.Add("ephemera");
        }

        // Explicitly historical or overtaken.
        if (Superseded().IsMatch(v))
        {
            adj -= 0.40;
            reasons.Add("superseded");
        }

        // Frontmatter + preamble only: a document header, not content.
        var hasFrontmatter = Frontmatter().IsMatch(v.TrimStart());
        var nchars = v.Length;
        if (hasFrontmatter && nchars < 900)
        {
            adj -= 0.55;
            reasons.Add("frontmatter-only");
        }
        else if (hasFrontmatter)
        {
            adj -= 0.25;
            reasons.Add("frontmatter-only");
        }

        var thin = false;
        if (nchars < 420)
        {
            adj -= 0.35;
            thin = true;
        }

        if (wordCount < 60)
        {
            adj -= 0.25;
            thin = true;
        }

        if (thin)
        {
            reasons.Add("thin-content");
        }

        var hi = archetype is ProvenanceArchetype.DocIndex or ProvenanceArchetype.TurnMirror
            ? PositiveHiCapped
            : PositiveHiDefault;

        return new ContentEvidence(Clamp(adj, Lo, hi), reasons);
    }

    private static IEnumerable<string> OtherProjectIds(string projectId, IReadOnlyList<string> allProjectIds) =>
        allProjectIds.Where(id => !string.Equals(id, projectId, StringComparison.Ordinal));

    private static double Per100(int count, int wordCount) => 100.0 * count / Math.Max(wordCount, 1);

    private static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_\-']*")]
    private static partial Regex Words();

    [GeneratedRegex(
        """\bnever\b|\balways\b|\bmust (?:still |be |not |name|make|raise)|\bdo not\b|\bdon't\b|\bdoes not\b(?=[^.]{0,40}\b(?:index|need|cover|apply))|\bprefer\b|\binvariant\b|\btraps?\b|\bgotchas?\b|\bbeware\b|\bmitigations?\b|\bworkarounds?\b|\brule\b|\bcontract\b|\bfuture (?:session|audit|reader|maintainer)|\bbefore proposing\b|\bnot (?:evidence|drift|duplication|debt)\b|\bis correct output\b|\bread (?:that|this|it) (?:comment |note )?before\b|\brecorded here\b|\bso nobody\b|\bexempt\b|\bby design\b|\bdeliberate(?:ly)?\b|\bon purpose\b|\brequired\b|\bsilently\b|\bfails? open\b|\bworse than\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex RuleLanguage();

    [GeneratedRegex(
        """\bmeasured\b|\bbest-of-\d|\bwall (?:clock|time)\b|\bbenchmark(?:ed|dotnet)?\b|\bobserved\b|\breproduc(?:ed|ible)\b|\bverified independently\b|\bp9[59]\b|\bthroughput\b|\bpeak rss\b|\bpassed?\b\s*\d|\bnDCG\b|\brecall@|\bMRR\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex MeasureWords();

    [GeneratedRegex(
        """\b\d+(?:[.,]\d+)?\s?(?:ms|s|sec|secs|seconds|m|min|mb|gb|kb|%|×|x|tokens?)\b|\b\d+(?:[.,]\d+)?\s?(?:tests?|chunks?|packages?|lines?|files?|commits?)\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex NumberWithUnit();

    [GeneratedRegex(
        """\bAC:|\bGate(?:s)?:|\bacceptance criteria\b|\beffort:|\bimpact:|\bevidence:\s*(?:MEASURED|INFERRED)|\bworktree\b|\bwave \d|\bsub-?agents?:|\bpersona\s*/\s*model|\bcloses:|\bdispatch(?:ed|ing)?\b|\bTDD: (?:yes|no)\b|\bowner (?:ruling|action|requirement)\b|\brisk if deferred\b|\breviewer lane\b|\blens (?:a\d|of)\b|\bthis lens\b|\bread-only (?:review|audit)\b|\bout of scope for\b|\bfollow-?ups? §|\bstatus:\s*(?:executed|open|verified)\b|\bPR #\d+|\bissue #\d+|\bPRs? #\d+|\b#\d{3,}\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Ephemera();

    [GeneratedRegex(
        """\bsupersed(?:ed|es|ing)\b|\bhistorical note\b|\bno longer\b|\bwas (?:reversed|retracted)\b|\bhistorical\)|\bthis was resolved\b|\bhas now (?:fired|lapsed)\b|\bpreviously lived\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Superseded();

    [GeneratedRegex(@"^\s*\|\s*\**\s*[A-Z]{1,3}[-]?\d{1,3}\b", RegexOptions.Multiline)]
    private static partial Regex FindingRow();

    [GeneratedRegex(@"^\s*\|", RegexOptions.Multiline)]
    private static partial Regex TableRow();

    [GeneratedRegex(@"\]\(")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[\w.\-/]+\.md\b")]
    private static partial Regex DocFilename();

    [GeneratedRegex(@"^---\s*\nupdated:", RegexOptions.Multiline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"^\s*\|\s*\d+\.\d+", RegexOptions.Multiline)]
    private static partial Regex VersionRow();
}
