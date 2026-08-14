using Microsoft.Data.Sqlite;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Derives structured-path -&gt; chunk hash maps directly from a regenerated corpus database
///     (WP4b, docs/plans/2026-08-14-code-quality-improvement-plan.md), replacing the retired
///     scripts/chunk-hash-map.json. That file encoded scripts/src/chunking.py's (the old Python
///     re-chunker's) structured-path hashes, byte-identical only to a fixture built through that
///     chunker; the production <see cref="AiRaccoon.Infrastructure.Ingestion.FileIngestor" />
///     (WP7's engine-aware budget) produces different chunk boundaries, so those hashes no
///     longer identify any real row. Mirrors
///     AiRaccoon.Tests.Integration.RrfParameterSweepTests.BuildHashMapFromCorpus.
/// </summary>
internal static class CorpusHashMap
{
    /// <summary>
    ///     ExpectedSource format is "prefix:relPath[#section]" (scripts/src/chunking.py's
    ///     `_chunk_path`); only the two prefixes scripts/baseline-queries.json uses are handled
    ///     here — an unrecognized prefix throws rather than silently mis-mapping a query to the
    ///     wrong chunk. Returns the exact-chunk hash per <paramref name="expectedSources" /> plus,
    ///     for each source's file, every hash the corpus actually carries for that file (multiple
    ///     chunks may now share a heading_path since WP7's token-budget chunker can split one
    ///     section into several chunks).
    /// </summary>
    public static (Dictionary<string, string> HashMap, Dictionary<string, HashSet<string>> FileHashes) Build(
        string dbPath, IEnumerable<string> expectedSources)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_file, heading_path, hash, chunk_index, value FROM entries " +
                               "ORDER BY source_file, chunk_index";
        using var reader = command.ExecuteReader();
        var rows = new List<(string SourceFile, string? HeadingPath, string Hash, int ChunkIndex, string Value)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetInt32(3), reader.GetString(4)));
        }

        var byFile = rows.GroupBy(row => row.SourceFile, StringComparer.Ordinal).ToList();
        var hashMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileHashes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var expected in expectedSources.Distinct(StringComparer.Ordinal))
        {
            var (relSuffix, section) = ParseExpectedSource(expected);
            var group = byFile.SingleOrDefault(g => g.Key.EndsWith(relSuffix, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException(
                            $"CorpusHashMap.Build: no source_file in the regenerated corpus ends with " +
                            $"'{relSuffix}' (expectedSource '{expected}')");
            var chunks = group.OrderBy(row => row.ChunkIndex).ToList();
            var match = section is null
                ? chunks[0]
                : chunks.FirstOrDefault(row => !string.IsNullOrEmpty(row.HeadingPath) && Slugify(row.HeadingPath) == section);
            if (match.Hash is null && section is not null)
            {
                // Fallback (measured on docs/adr/0046-retire-nfr-1-llm-cost-protection.md, "A5"): the
                // chunker occasionally drops a heading line that is immediately followed by a
                // one-line section body, so the chunk carries no heading_path at all. Match the
                // section name as a line prefix in the chunk body instead.
                match = chunks.FirstOrDefault(row =>
                    string.IsNullOrEmpty(row.HeadingPath)
                    && row.Value.Contains($"{section}:", StringComparison.OrdinalIgnoreCase));
            }

            if (match.Hash is null)
            {
                throw new InvalidOperationException(
                    $"CorpusHashMap.Build: no chunk of '{relSuffix}' has heading_path '{section}' " +
                    $"(expectedSource '{expected}')");
            }

            hashMap[expected] = match.Hash;
            fileHashes[FileKey(expected)] = [.. chunks.Select(row => row.Hash)];
        }

        return (hashMap, fileHashes);
    }

    /// <summary>'docs:adr:0011-frontend-chassis-stack.md#decision' -&gt; 'docs:adr:0011-frontend-chassis-stack.md'.</summary>
    public static string FileKey(string structuredPath) => structuredPath.Split('#')[0];

    /// <summary>
    ///     Normalizes a corpus `heading_path` value (e.g. "ADR-0011 &gt; Context", "Consequences
    ///     (overall)", "Alternatives considered") to the same slug shape as an expectedSource's
    ///     '#section' part (e.g. "context", "consequences", "alternatives-considered"): the segment
    ///     after the last " &gt; ", minus any trailing " (...)" qualifier, lowercased with spaces
    ///     turned into hyphens.
    /// </summary>
    private static string Slugify(string headingPath)
    {
        var separatorIndex = headingPath.LastIndexOf(" > ", StringComparison.Ordinal);
        var tail = separatorIndex < 0 ? headingPath : headingPath[(separatorIndex + 3)..];
        var parenIndex = tail.IndexOf(" (", StringComparison.Ordinal);
        if (parenIndex >= 0)
        {
            tail = tail[..parenIndex];
        }

        return tail.Trim().ToLowerInvariant().Replace(' ', '-');
    }

    /// <summary>Reverses scripts/src/chunking.py's `_chunk_path`/`_source_prefix`/`_short_rel` for the
    /// two prefixes scripts/baseline-queries.json uses: "docs:adr:X.md[#section]" and
    /// "ai-badger:invariants/X.md" (no section — that file type never splits by heading).</summary>
    private static (string RelSuffix, string? Section) ParseExpectedSource(string expectedSource)
    {
        const string AdrPrefix = "docs:adr:";
        const string InvariantPrefix = "ai-badger:invariants/";

        var hashIndex = expectedSource.IndexOf('#');
        var relPart = hashIndex < 0 ? expectedSource : expectedSource[..hashIndex];
        var section = hashIndex < 0 ? null : expectedSource[(hashIndex + 1)..];

        if (relPart.StartsWith(AdrPrefix, StringComparison.Ordinal))
        {
            return ($"docs/adr/{relPart[AdrPrefix.Length..]}", section);
        }

        if (relPart.StartsWith(InvariantPrefix, StringComparison.Ordinal))
        {
            return ($".ai-badger/invariants/{relPart[InvariantPrefix.Length..]}", section);
        }

        throw new InvalidOperationException(
            $"CorpusHashMap.ParseExpectedSource: unrecognized prefix in '{expectedSource}' — add a case if a " +
            "new expectedSource needs it.");
    }
}
