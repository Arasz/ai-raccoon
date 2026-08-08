using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory;

/// <summary>Shape-level evidence extracted once per candidate and shared by every scoring branch
/// (ported from agentC/scorer.py's features(), see docs/adr/0018-promotion-scoring-v2.md v3 section).</summary>
internal readonly record struct CandidateFeatures(
    double RuleDensity,
    int MeasureWords,
    int NumUnit,
    double Ephemera,
    bool Superseded,
    int FindingRows,
    double TableFrac,
    double LinkDensity,
    double DocnameDensity,
    int VersionRows,
    bool Frontmatter,
    int NChars,
    int NWords,
    bool MidSentence,
    bool HeadingStart,
    int ForeignProjects,
    bool ForeignSubject,
    bool StatusOpener,
    int StatusVocab,
    bool SecondPerson,
    int CommitHashes,
    int RealMeasures,
    int DurableLoose,
    bool DatedFact,
    double FirstPerson,
    int MetaHeader,
    int Imperatives,
    int Urls,
    bool ContentsIndex,
    bool DirReadme);

internal static partial class CandidateFeatureExtractor
{
    private const int ForeignSubjectHeadChars = 250;
    private const int StatusOpenerHeadChars = 80;
    private const int DatedFactWindowChars = 120;

    /// <summary>Alternate spellings a project's id is written under in free text — content matching
    /// is otherwise a bare-substring check that misses e.g. "airaccoon" for "ai-raccoon" (ported from
    /// agentC/scorer.py's PROJECT_ALIASES, docs/work/promotion-scoring-eval/round2/agentC/scorer.py).</summary>
    private static readonly IReadOnlyDictionary<string, string[]> ProjectAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ai-badger"] = ["ai-badger", "ai_badger", "badger_lib", "welcome-ai-badger"],
            ["ai-raccoon"] = ["ai-raccoon", "airaccoon", "ai_raccoon"],
            ["jsaa"] = ["jsaa", "job-search-ai-assistant", "jobsearchaiassistant"],
            ["hermes-default"] = ["hermes"],
            ["arasz-home-page"] = ["arasz-home-page", "arasz.dev", "home-page"]
        };

    private static IReadOnlyList<string> AliasesFor(string projectId) =>
        ProjectAliases.TryGetValue(projectId, out var aliases) ? aliases : [projectId];

    internal static CandidateFeatures Extract(string value, string projectId, IReadOnlyList<string> allProjectIds)
    {
        var v = value ?? string.Empty;
        var nWords = Words().Matches(v).Count;
        var lines = v.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var lineCount = Math.Max(lines.Count, 1);

        var tableRowCount = lines.Count(l => TableRow().IsMatch(l));
        var others = allProjectIds.Where(id => !string.Equals(id, projectId, StringComparison.Ordinal)).ToList();
        var head = v.Length > ForeignSubjectHeadChars ? v[..ForeignSubjectHeadChars] : v;

        var openerHead = v.TrimStart('*', '#', '>', '-', '–', ' ', '\t', '\n');
        openerHead = openerHead.Length > StatusOpenerHeadChars ? openerHead[..StatusOpenerHeadChars] : openerHead;

        var withoutTestCounts = TestCounts().Replace(v, " ");

        var datedWindow = v.Length > DatedFactWindowChars ? v[..DatedFactWindowChars] : v;

        var stripped = v.TrimStart();
        var firstChar = stripped.Length > 0 ? stripped[0] : '\0';

        return new CandidateFeatures(
            RuleDensity: Per100(RuleLanguage().Matches(v).Count, nWords),
            MeasureWords: MeasureWordsRegex().Matches(v).Count,
            NumUnit: NumberWithUnit().Matches(v).Count,
            Ephemera: Per100(Ephemera().Matches(v).Count, nWords),
            Superseded: Superseded().IsMatch(v),
            FindingRows: FindingRow().Matches(v).Count,
            TableFrac: (double)tableRowCount / lineCount,
            LinkDensity: Per100(MarkdownLink().Matches(v).Count, nWords),
            DocnameDensity: Per100(DocFilename().Matches(v).Count, nWords),
            VersionRows: VersionRow().Matches(v).Count,
            Frontmatter: Frontmatter().IsMatch(v.TrimStart()),
            NChars: v.Length,
            NWords: nWords,
            MidSentence: char.IsLower(firstChar) || firstChar is ')' or ',' or ';',
            HeadingStart: stripped.StartsWith('#'),
            ForeignProjects: others.Count(id =>
                AliasesFor(id).Any(alias => v.Contains(alias, StringComparison.OrdinalIgnoreCase))),
            ForeignSubject: others.Any(id =>
                AliasesFor(id).Any(alias => head.Contains(alias, StringComparison.OrdinalIgnoreCase))),
            StatusOpener: StatusOpener().IsMatch(openerHead),
            StatusVocab: StatusVocabulary().Matches(v).Count,
            SecondPerson: SecondPerson().IsMatch(v),
            CommitHashes: CommitHash().Matches(v).Count,
            RealMeasures: MeasureUnitsLoose().Matches(withoutTestCounts).Count,
            DurableLoose: DurableLoose().Matches(v).Count,
            DatedFact: DatedFact().IsMatch(datedWindow),
            FirstPerson: Per100(FirstPerson().Matches(v).Count, nWords),
            MetaHeader: MetaHeaderLine().Matches(v).Count,
            Imperatives: ImperativeItem().Matches(v).Count,
            Urls: Url().Matches(v).Count,
            ContentsIndex: ContentsHeader().IsMatch(v),
            DirReadme: DirReadme().IsMatch(v.TrimStart()));
    }

    private static double Per100(int count, int nWords) => 100.0 * count / Math.Max(nWords, 1);

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_\-']*")]
    private static partial Regex Words();

    [GeneratedRegex(
        """\bnever\b|\balways\b|\bmust (?:still |be |not |name|make|raise|treat|come|already|exist)|\bdo not\b|\bdon't\b|\bdoes not apply\b|(?<!\bI )(?<!we )\bcannot\b|\bprefer\b|\binvariant\b|\btraps?\b|\bgotchas?\b|\bbeware\b|\bmitigations?\b|\bworkarounds?\b|\brule\b|\bcontract\b|\bsemantics\b|\bprecedence\b|\bby design\b|\bdeliberate(?:ly)?\b|\bon purpose\b|\bsilently\b|\bfails? open\b|\bworse than\b|\bREGRESSION\b|\broot cause\b|\baccepted, not a defect\b|\bnot a defect\b|\bexempt\b|\bso nobody\b|\brecorded here\b|\bfuture (?:session|audit|reader|maintainer)|\binvisible to\b|\bhides?\b(?=[^.]{0,60}\b(?:from|rows|content|search))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex RuleLanguage();

    [GeneratedRegex(
        """\bmeasured\b|\bbest-of-\d|\bwall (?:clock|time)\b|\bbenchmark(?:ed|dotnet)?\b|\bobserved\b|\breproduc(?:ed|ible)\b|\bverified\b|\bp9[59]\b|\bthroughput\b|\bpeak rss\b|\bnDCG\b|\brecall@|\bMRR\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex MeasureWordsRegex();

    [GeneratedRegex(
        """\b\d+(?:[.,]\d+)?\s?(?:ms|s|sec|secs|seconds|min|mb|gb|kb|%|×|x)\b|\b\d+(?:[.,]\d+)?\s?(?:chunks?|packages?|files?|entries|rows)\b""",
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

    [GeneratedRegex(@"^\s*\|")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"\]\(")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"[\w.\-/]+\.md\b")]
    private static partial Regex DocFilename();

    [GeneratedRegex(@"^---\s*\n(?:updated|name|version):", RegexOptions.Multiline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"^\s*\|\s*\d+\.\d+", RegexOptions.Multiline)]
    private static partial Regex VersionRow();

    [GeneratedRegex(
        """^\s*(Done\b|Fixed\b|Verified\b|Merged\b|Pinned\b|Recorded\b|Rendered\b|All confirmed|Status\b|Prep done|Shell ready|Task closed|Cleanup complete|Amended\b|Locked in|Draft PR|Fresh verification|No code has changed|f: |Implementation in flight|Extension complete|Rollout complete|Ad-hoc verification|Render(?:\s|ing)|Plan written|Software MoE|Here.s the current|PR[- #]|\*\*1\.|All verification|Verification (?:confirmed|complete)|[A-Z][\w' -]{0,30}\b(?:complete|done|closed|finished|delivered)\b\s*[.:!—–-])""",
        RegexOptions.IgnoreCase)]
    private static partial Regex StatusOpener();

    [GeneratedRegex(
        """\b(merged|pushed|re-pushed|in flight|worktree|pre-push|gate chain|full suite|suite green|passed\b|skipped\b|exit 0|handed over|dispatched|still working|waiting on|re-run|rebased|cherry-pick|draft PR|ready for review|cron job|status board|deliverable|committed as|origin/main|HEAD|CI checks)\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex StatusVocabulary();

    [GeneratedRegex(@"\b(your|you'll|you can|as instructed|per your)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecondPerson();

    [GeneratedRegex(@"\b[0-9a-f]{7,10}\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommitHash();

    [GeneratedRegex(
        @"\b\d+\s*(passed|failed|skipped|errors|warnings|green|assertions?)\b|\bexit 0\b|\b\d+/\d+\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TestCounts();

    [GeneratedRegex(@"\d+(?:\.\d+)?\s*(?:s|ms|x|%|lines?|KB|MB|GB|tokens?)\b")]
    private static partial Regex MeasureUnitsLoose();

    [GeneratedRegex(
        """\[facts\]|\bREGRESSION\b|\bgotcha\b|\broot cause\b|\brequires?\b|\bmust\b|\bnever\b|\balways\b|\bconvention\b|\bone \w+ per \w+|\bholds\b|\bby design\b|\bcontract\b|\bprecedence\b|\bsemantics\b|\bmeans\b[^.\n]{0,40}\bnot\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex DurableLoose();

    [GeneratedRegex(@"\(\d{4}-\d{2}-\d{2}\)[:,]|\bverified \d{4}(?:-\d{2})?\b|\(verified ")]
    private static partial Regex DatedFact();

    [GeneratedRegex(@"\bI\b|\bmy\b|\bme\b")]
    private static partial Regex FirstPerson();

    [GeneratedRegex(@"https?://")]
    private static partial Regex Url();

    [GeneratedRegex(@"^#{1,3}\s*Contents\b", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ContentsHeader();

    [GeneratedRegex(@"\A#\s+[\w.\-]+/\s*$", RegexOptions.Multiline)]
    private static partial Regex DirReadme();

    [GeneratedRegex(
        """^\s*\*{0,2}(Task|Project|Worktree|Branch(?:/worktree)?|Date|Reviewer|Draft|Owner|Updated|Version|Corpus|Scope)\s*[:*]""",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex MetaHeaderLine();

    [GeneratedRegex(
        """^\s*(?:\d+\.|[-*])\s+(?:Review|Reconcile|Run|Submit|Report|Finish|Fix|Update|Rebase|Merge|Check|Verify|Register|Grep|Add|Remove|Delete|Rename|Move)\b""",
        RegexOptions.Multiline)]
    private static partial Regex ImperativeItem();
}
