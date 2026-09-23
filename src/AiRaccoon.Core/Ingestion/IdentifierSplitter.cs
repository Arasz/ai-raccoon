using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Splits an identifier into its lowercase word parts, and extracts the multi-part identifiers
///     embedded in a code chunk for <c>code_entries.identifiers</c> (docs/adr/0108). <see cref="Split" />
///     matches the Python reference `split_identifier` (scripts/src/retrieval_tuning/code_corpus.py)
///     byte-for-byte: boundaries are separators (<c>_ - .</c> and whitespace), a lower-to-upper case
///     transition, an acronym run followed by a new capitalised word, and letter/digit transitions.
/// </summary>
public static partial class IdentifierSplitter
{
    private const char Boundary = '\x00';

    /// <summary>One identifier's lowercase word parts, in order.</summary>
    public static IReadOnlyList<string> Split(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return [];
        }

        List<string> tokens = [];
        foreach (var chunk in SeparatorRegex().Split(name))
        {
            if (chunk.Length == 0)
            {
                continue;
            }

            tokens.AddRange(SplitCaseAndDigits(chunk));
        }

        return [.. tokens.Where(t => t.Length > 0).Select(t => t.ToLowerInvariant())];
    }

    /// <summary>
    ///     Every identifier-like token in <paramref name="chunkText" /> that splits into 2 or more
    ///     parts, space-joined, deduplicated, and newline-joined — the value stored in
    ///     <c>code_entries.identifiers</c>.
    /// </summary>
    public static string Identifiers(string chunkText)
    {
        if (string.IsNullOrEmpty(chunkText))
        {
            return "";
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> kept = [];
        foreach (Match match in TokenRegex().Matches(chunkText))
        {
            var parts = Split(match.Value);
            if (parts.Count < 2)
            {
                continue;
            }

            var joined = string.Join(' ', parts);
            if (seen.Add(joined))
            {
                kept.Add(joined);
            }
        }

        return string.Join('\n', kept);
    }

    private static IEnumerable<string> SplitCaseAndDigits(string chunk)
    {
        var marked = LowerToUpperRegex().Replace(chunk, MarkBoundary);
        marked = AcronymToWordRegex().Replace(marked, MarkBoundary);
        marked = LetterToDigitRegex().Replace(marked, MarkBoundary);
        marked = DigitToLetterRegex().Replace(marked, MarkBoundary);
        return marked.Split(Boundary, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string MarkBoundary(Match match) => $"{match.Groups[1].Value}{Boundary}{match.Groups[2].Value}";

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_\-]*[A-Za-z0-9]|[A-Za-z]")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[_\-.\s]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex LowerToUpperRegex();

    [GeneratedRegex(@"([A-Z]+)([A-Z][a-z])")]
    private static partial Regex AcronymToWordRegex();

    [GeneratedRegex(@"([A-Za-z])([0-9])")]
    private static partial Regex LetterToDigitRegex();

    [GeneratedRegex(@"([0-9])([A-Za-z])")]
    private static partial Regex DigitToLetterRegex();
}
