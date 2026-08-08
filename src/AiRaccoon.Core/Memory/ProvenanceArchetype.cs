using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory;

/// <summary>What kind of document a shared-extraction candidate came out of (docs/adr/0018-promotion-scoring-v2.md).</summary>
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
    CatalogPage,
    ChangelogEntry,
    Plan,
    Review,
    DocIndex,
    TurnMirror
}

/// <summary>Classifies a candidate's provenance archetype from its path/source-file shape (ported from
/// agentB/scorer.py's archetype(), see docs/adr/0018-promotion-scoring-v2.md). First-match-wins ordering.</summary>
internal static partial class ProvenanceArchetypeClassifier
{
    private static readonly IReadOnlyDictionary<ProvenanceArchetype, double> Priors =
        new Dictionary<ProvenanceArchetype, double>
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

    internal static double Prior(ProvenanceArchetype archetype) => Priors[archetype];

    /// <summary>Kebab-case reason tag for the archetype, e.g. "organic-note".</summary>
    internal static string Tag(ProvenanceArchetype archetype) => archetype switch
    {
        ProvenanceArchetype.OrganicNote => "organic-note",
        ProvenanceArchetype.Adr => "adr",
        ProvenanceArchetype.Charter => "charter",
        ProvenanceArchetype.Explanation => "explanation",
        ProvenanceArchetype.Measurement => "measurement",
        ProvenanceArchetype.ResearchSynthesis => "research-synthesis",
        ProvenanceArchetype.Reference => "reference",
        ProvenanceArchetype.WorkNote => "work-note",
        ProvenanceArchetype.CatalogPage => "catalog-page",
        ProvenanceArchetype.ChangelogEntry => "changelog-entry",
        ProvenanceArchetype.Plan => "plan",
        ProvenanceArchetype.Review => "review",
        ProvenanceArchetype.DocIndex => "doc-index",
        ProvenanceArchetype.TurnMirror => "turn-mirror",
        _ => throw new ArgumentOutOfRangeException(nameof(archetype))
    };

    internal static ProvenanceArchetype Classify(string path, string? sourceFile, string value)
    {
        var p = (path ?? string.Empty).ToLowerInvariant();
        var basename = Basename(p);

        // A mirror of an agent's own tool-call turn: the payload is a transcript, not an entry.
        if (TurnMirrorMarkup().Matches(value).Count >= 2)
        {
            return ProvenanceArchetype.TurnMirror;
        }

        // Agent-authored memory write: no ingest source, or a content-addressed filename.
        if (string.IsNullOrEmpty(sourceFile) || HexName().IsMatch(basename))
        {
            return ProvenanceArchetype.OrganicNote;
        }

        // Directory indexes and rolling ledgers: rows of pointers to other docs.
        if (basename is "readme.md" or "changelog.md" or "index.md")
        {
            return ProvenanceArchetype.DocIndex;
        }

        // A numbered decision record: docs/adr/, or "NNNN-slug.md" that is not a dated work note.
        if (p.Contains("/adr/", StringComparison.Ordinal) || p.Contains("/decisions/", StringComparison.Ordinal) ||
            (NumberedPrefix().IsMatch(basename) && !DatedPrefix().IsMatch(basename)))
        {
            return ProvenanceArchetype.Adr;
        }

        if (basename.Contains("charter", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.Charter;
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
            basename.EndsWith("plan.md", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.Plan;
        }

        if (p.Contains("/reviews/", StringComparison.Ordinal) || basename.Contains("review", StringComparison.Ordinal) ||
            basename.Contains("moe-", StringComparison.Ordinal) || LensFilePrefix().IsMatch(basename) ||
            basename.Contains("findings", StringComparison.Ordinal) || basename.Contains("incident", StringComparison.Ordinal))
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

        if (p.Contains("/guide", StringComparison.Ordinal) ||
            basename is "skills.md" or "getting-started.md" or "glossary.md")
        {
            return ProvenanceArchetype.CatalogPage;
        }

        if (p.Contains("/changelog/", StringComparison.Ordinal) || p.Contains("/releases/", StringComparison.Ordinal))
        {
            return ProvenanceArchetype.ChangelogEntry;
        }

        return ProvenanceArchetype.WorkNote;
    }

    private static string Basename(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 ? path[(idx + 1)..] : path;
    }

    [GeneratedRegex(@"^[0-9a-f]{32,}\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex HexName();

    [GeneratedRegex(
        """</?(invoke|parameter|content|sourceFile|antml:invoke|function_calls)\b|<invoke\s+name=|<parameter\s+name=""",
        RegexOptions.IgnoreCase)]
    private static partial Regex TurnMirrorMarkup();

    [GeneratedRegex(@"^\d{4}-")]
    private static partial Regex NumberedPrefix();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex DatedPrefix();

    [GeneratedRegex(@"^[a-z]\d+-")]
    private static partial Regex LensFilePrefix();
}
