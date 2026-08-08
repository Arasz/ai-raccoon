using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory;

/// <summary>Refined score plus the plain-name reason tags the organic refinement layer fired.</summary>
internal readonly record struct OrganicRefinementResult(double Score, IReadOnlyList<string> Reasons);

/// <summary>Refines the archetype+evidence score for organic (source_file-null) entries: without it, the
/// model saturates on the organic prior and cannot tell a status/turn-mirror dump ("Done. Everything
/// shipped... 3272 passed, 3m18s") from a durable fact — held-out Spearman on the organic-only slice was
/// +0.145 (see docs/adr/0018-promotion-scoring-v2.md). Ported from refine.py.</summary>
internal static partial class OrganicRefinement
{
    private const double DeltaLo = -2.8;
    private const double DeltaHi = 1.5;
    private const double ShortDefinitionalFloor = 2.2;
    private const int ShortDefinitionalMaxWords = 40;
    private const int OpenerWindowChars = 80;
    private const int DatedFactWindowChars = 120;

    internal static OrganicRefinementResult Apply(double baseScore, string? sourceFile, string value)
    {
        if (sourceFile is not null)
        {
            return new OrganicRefinementResult(baseScore, []);
        }

        var v = value ?? string.Empty;
        var reasons = new List<string>();
        var delta = 0.0;

        var opener = v.Length > OpenerWindowChars ? v[..OpenerWindowChars] : v;
        if (StatusOpener().IsMatch(opener))
        {
            delta -= 1.2;
            reasons.Add("status-opener");
        }

        var statusHits = StatusVocabulary().Matches(v).Count;
        if (statusHits > 0)
        {
            delta -= Math.Min(1.5, 0.15 * statusHits);
            reasons.Add("status-vocabulary");
        }

        if (SecondPerson().IsMatch(v))
        {
            delta -= 0.5;
            reasons.Add("second-person");
        }

        if (CommitHash().Matches(v).Count >= 2)
        {
            delta -= 0.3;
            reasons.Add("commit-hashes");
        }

        // Test-result counts ("174 passed", "exit 0") are status, not measurements — strip them
        // before counting real measured evidence.
        var withoutTestCounts = TestCounts().Replace(v, " ");
        var realMeasures = Measure().Matches(withoutTestCounts).Count;
        if (realMeasures >= 2)
        {
            delta += 0.5;
            reasons.Add("real-measurements");
        }

        var durableHits = Durable().Matches(v).Count;
        if (durableHits > 0)
        {
            delta += Math.Min(1.0, 0.35 * durableHits);
            reasons.Add("durable-fact-language");
        }

        var datedWindow = v.Length > DatedFactWindowChars ? v[..DatedFactWindowChars] : v;
        if (DatedFact().IsMatch(datedWindow))
        {
            delta += 0.5;
            reasons.Add("dated-fact");
        }

        delta = Math.Clamp(delta, DeltaLo, DeltaHi);

        var wordCount = v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < ShortDefinitionalMaxWords && durableHits >= 1 && statusHits <= 1)
        {
            baseScore = Math.Max(baseScore, ShortDefinitionalFloor);
            reasons.Add("short-definitional-floor");
        }

        var score = Math.Clamp(baseScore + delta, 0.0, 4.0);
        return new OrganicRefinementResult(score, reasons);
    }

    [GeneratedRegex(
        """^\s*(Done\b|Fixed\b|Verified\b|Merged\b|Pinned\b|Recorded\b|Rendered\b|All confirmed|Status\b|Prep done|Shell ready|Task closed|Cleanup complete|Amended\b|Locked in|Draft PR|Fresh verification|No code has changed|f: |Implementation in flight|Extension complete|Rollout complete|Both\b|Ad-hoc verification|Render(?:\s|ing)|Plan written|Software MoE|From\b|Here.s the current|PR[- #]|\*\*1\.)""",
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
    private static partial Regex Measure();

    [GeneratedRegex(
        """\[facts\]|\bREGRESSION\b|\bgotcha\b|\broot cause\b|\brequires?\b|\bmust\b|\bnever\b|\balways\b|\bconvention\b|\bone \w+ per \w+|\bholds\b|\bby design\b|\bcontract\b|\bprecedence\b|\bsemantics\b""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Durable();

    [GeneratedRegex(@"\(\d{4}-\d{2}-\d{2}\)[:,]")]
    private static partial Regex DatedFact();
}
