using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory;

/// <summary>What provenance channel a shared-extraction candidate came from (docs/adr/0018-promotion-scoring-v2.md).</summary>
internal enum ProvenanceArchetype
{
    OrganicNote,
    Adr,
    Charter,
    Explanation,
    Measurement,
    ResearchSynthesis,
    Reference,
    WorkNote,
    OtherDoc,
    CatalogPage,
    ChangelogEntry,
    Plan,
    Review,
    DocIndex,
    TurnMirror,
    Transcript,
    RememberLog,
    AutoMemorySession,
    AutoMemoryIndex,
    AutoMemoryNote
}

/// <summary>
///     Classifies a candidate's provenance channel from its path/`source_file` shape (ported from
///     scorer.py's channel(), see docs/adr/0018-promotion-scoring-v2.md round-3 lane-A section).
///     First-match-wins ordering.
/// </summary>
internal static partial class ProvenanceArchetypeClassifier
{
    /// <summary>
    ///     Priors are fitted, not hand-set — each channel's labelled mean, shrunk toward the
    ///     corpus mean and corrected by the evidence layer's own mean adjustment inside that channel
    ///     (see METHOD.md for the derivation). The six structural-noise channels below the 0.4 promotion
    ///     floor are hand-set instead: every labelled row in them is a 0.
    /// </summary>
    private static readonly IReadOnlyDictionary<ProvenanceArchetype, double> Priors =
        new Dictionary<ProvenanceArchetype, double>
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

    /// <summary>
    ///     Channels whose payload is a considered technical document. The portability layer
    ///     only applies here: elsewhere a technology name is incidental.
    /// </summary>
    internal static readonly HashSet<ProvenanceArchetype> DocFamily =
    [
        ProvenanceArchetype.Adr,
        ProvenanceArchetype.Charter,
        ProvenanceArchetype.Explanation,
        ProvenanceArchetype.Measurement,
        ProvenanceArchetype.ResearchSynthesis,
        ProvenanceArchetype.Reference
    ];

    internal static double Prior(ProvenanceArchetype archetype) => Priors[archetype];

    /// <summary>Kebab-case reason tag for the archetype, e.g. "auto-memory-note".</summary>
    internal static string Tag(ProvenanceArchetype archetype) =>
        archetype switch
        {
            ProvenanceArchetype.OrganicNote => "organic-note",
            ProvenanceArchetype.Adr => "adr",
            ProvenanceArchetype.Charter => "charter",
            ProvenanceArchetype.Explanation => "explanation",
            ProvenanceArchetype.Measurement => "measurement",
            ProvenanceArchetype.ResearchSynthesis => "research-synthesis",
            ProvenanceArchetype.Reference => "reference",
            ProvenanceArchetype.WorkNote => "work-note",
            ProvenanceArchetype.OtherDoc => "other-doc",
            ProvenanceArchetype.CatalogPage => "catalog-page",
            ProvenanceArchetype.ChangelogEntry => "changelog-entry",
            ProvenanceArchetype.Plan => "plan",
            ProvenanceArchetype.Review => "review",
            ProvenanceArchetype.DocIndex => "doc-index",
            ProvenanceArchetype.TurnMirror => "turn-mirror",
            ProvenanceArchetype.Transcript => "transcript",
            ProvenanceArchetype.RememberLog => "remember-log",
            ProvenanceArchetype.AutoMemorySession => "auto-memory-session",
            ProvenanceArchetype.AutoMemoryIndex => "auto-memory-index",
            ProvenanceArchetype.AutoMemoryNote => "auto-memory-note",
            _ => throw new ArgumentOutOfRangeException(nameof(archetype))
        };

    internal static ProvenanceArchetype Classify(string path, string? sourceFile, string value)
    {
        var (_, isMirror) = TurnMirrorPrefix.Split(value ?? string.Empty);
        if (isMirror)
        {
            return ProvenanceArchetype.TurnMirror;
        }

        var src = sourceFile ?? string.Empty;
        if (TranscriptSource().IsMatch(src))
        {
            return ProvenanceArchetype.Transcript;
        }

        // The store mints a hex filename for every agent write, so a hex `path` means "organic
        // write", not "document chunk" — checked before any source_file-shaped routing, since
        // source_file on such a row is a citation, not a provenance (METHOD.md §7).
        var rawPath = path ?? string.Empty;
        if (string.IsNullOrEmpty(sourceFile) || HexName().IsMatch(Basename(rawPath.ToLowerInvariant())))
        {
            return ProvenanceArchetype.OrganicNote;
        }

        // Past this point source_file is a real path and the only provenance worth reading.
        var p = src.ToLowerInvariant();
        var basename = Basename(p);

        if (p.Contains("/.remember/", StringComparison.Ordinal) || p.StartsWith(".remember/", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.RememberLog;
        }

        if (p.Contains("/.claude/", StringComparison.Ordinal) && p.Contains("/memory/", StringComparison.Ordinal))
        {
            if (basename == "memory.md")
            {
                return ProvenanceArchetype.AutoMemoryIndex;
            }

            if (basename.StartsWith("session", StringComparison.Ordinal) ||
                basename.Contains("status", StringComparison.Ordinal) ||
                basename.Contains("handoff", StringComparison.Ordinal))
            {
                return ProvenanceArchetype.AutoMemorySession;
            }

            return ProvenanceArchetype.AutoMemoryNote;
        }

        if (basename is "readme.md" or "changelog.md" or "index.md")
        {
            return ProvenanceArchetype.DocIndex;
        }

        if (p.Contains("/adr/", StringComparison.Ordinal) || p.Contains("/decisions/", StringComparison.Ordinal) ||
            NumberedPrefix().IsMatch(basename) && !DatedPrefix().IsMatch(basename))
        {
            return ProvenanceArchetype.Adr;
        }

        if (basename.Contains("charter", StringComparison.Ordinal))
        {
            // A dated charter under docs/work is in-flight review coordination, not a durable charter.
            return DatedPrefix().IsMatch(basename) ? ProvenanceArchetype.Review : ProvenanceArchetype.Charter;
        }

        if (p.Contains("/explanation/", StringComparison.Ordinal) ||
            p.Contains("/design/", StringComparison.Ordinal) ||
            basename.Contains("architecture", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.Explanation;
        }

        if (p.Contains("/plans/", StringComparison.Ordinal) ||
            basename.StartsWith("plan", StringComparison.Ordinal) ||
            basename.Contains("-plan", StringComparison.Ordinal) ||
            basename.EndsWith("plan.md", StringComparison.Ordinal) ||
            basename.Contains("backlog", StringComparison.Ordinal) ||
            basename.Contains("checkpoint", StringComparison.Ordinal) ||
            basename.Contains("dev-review", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.Plan;
        }

        if (p.Contains("/reviews/", StringComparison.Ordinal) || basename.Contains("review", StringComparison.Ordinal) ||
            basename.Contains("moe-", StringComparison.Ordinal) || LensFilePrefix().IsMatch(basename) ||
            basename.Contains("findings", StringComparison.Ordinal) || basename.Contains("incident", StringComparison.Ordinal) ||
            basename.Contains("diagnosis", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.Review;
        }

        if (basename.Contains("sweep", StringComparison.Ordinal) || basename.Contains("benchmark", StringComparison.Ordinal) ||
            basename.Contains("-perf", StringComparison.Ordinal) || basename.Contains("perf-", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.Measurement;
        }

        if (p.Contains("/archive/", StringComparison.Ordinal) || basename.Contains("research", StringComparison.Ordinal) ||
            basename.Contains("synthesis", StringComparison.Ordinal) || basename.Contains("report", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.ResearchSynthesis;
        }

        if (p.Contains("/reference/", StringComparison.Ordinal) || p.Contains("/how-to/", StringComparison.Ordinal) ||
            p.Contains("/tutorial/", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.Reference;
        }

        if (p.Contains("/changelog/", StringComparison.Ordinal) || p.Contains("/releases/", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.ChangelogEntry;
        }

        if (p.Contains("/guide", StringComparison.Ordinal) ||
            basename is "skills.md" or "getting-started.md" or "glossary.md")
        {
            return ProvenanceArchetype.CatalogPage;
        }

        if (p.Contains("/docs/work/", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.WorkNote;
        }

        if (p.Contains("/docs/", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.OtherDoc;
        }

        return ProvenanceArchetype.WorkNote;
    }

    private static string Basename(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    /// <summary>
    ///     A Hermes conversation id used as a source, e.g. `hermes/20260809_125502_0d3cd9` — the
    ///     one case where `source_file` on a hex-path (organic) row carries real, negative information
    ///     (METHOD.md §7): a chat dump, whatever it looks like.
    /// </summary>
    [GeneratedRegex(@"(^|/)hermes/(\d{6,}_|[0-9a-f]{8}-[0-9a-f]{4}-)", RegexOptions.IgnoreCase)]
    private static partial Regex TranscriptSource();

    [GeneratedRegex(@"^[0-9a-f]{32,}(?:\.md)?$", RegexOptions.IgnoreCase)]
    private static partial Regex HexName();

    [GeneratedRegex(@"^\d{4}-")]
    private static partial Regex NumberedPrefix();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex DatedPrefix();

    [GeneratedRegex(@"^[a-z]\d+-")]
    private static partial Regex LensFilePrefix();
}
