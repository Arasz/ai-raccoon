using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>One indexed chunk of a graded corpus, as the retrieval gates read it back.</summary>
public sealed record CorpusChunk(string Hash, string SourceFile, string Value);

/// <summary>
///     Resolves a query's relevance set from an answer span instead of a chunk id (docs/adr/0077,
///     "what would make this adjudicable" item 3). A chunk is relevant when it comes from the
///     expected file and carries the graded answer text, so the set stays defined across chunking
///     arms that delete chunk ids and stays the same size across arms that fragment a file.
/// </summary>
public static class SpanAnchoredRelevance
{
    /// <summary>
    ///     The chunks of <paramref name="expectedSource" /> whose text contains
    ///     <paramref name="answerSpan" />, compared with whitespace runs collapsed so a re-wrapped or
    ///     header-prefixed row still matches. Throws when the file is absent or no chunk carries the
    ///     span — a silently empty relevance set scores 0 on every arm and reads as bad retrieval
    ///     when it means broken ground truth.
    /// </summary>
    public static IReadOnlySet<string> Resolve(
        IEnumerable<CorpusChunk> corpus, string expectedSource, string answerSpan)
    {
        Guard.IsNotNull(corpus);
        Guard.IsNotNullOrWhiteSpace(expectedSource);
        Guard.IsNotNullOrWhiteSpace(answerSpan);

        var ofFile = corpus.Where(chunk => IsFromFile(chunk.SourceFile, expectedSource)).ToList();
        if (ofFile.Count == 0)
        {
            throw new InvalidOperationException(
                $"SpanAnchoredRelevance: no chunk in the corpus comes from '{expectedSource}'.");
        }

        var needle = Collapse(answerSpan);
        var relevant = ofFile
            .Where(chunk => Collapse(chunk.Value).Contains(needle, StringComparison.Ordinal))
            .Select(chunk => chunk.Hash)
            .ToHashSet(StringComparer.Ordinal);

        if (relevant.Count == 0)
        {
            throw new InvalidOperationException(
                $"SpanAnchoredRelevance: no chunk of '{expectedSource}' ({ofFile.Count} chunks) carries the " +
                $"answer span '{Truncate(answerSpan)}'. Either the span straddles a chunk boundary under this " +
                "arm — a real finding to record, not to paper over — or the graded span no longer matches the source.");
        }

        return relevant;
    }

    /// <summary>Matches a corpus <c>source_file</c> against a repo-relative expected path, tolerating the
    /// absolute or extraction-rooted prefix an ingest may have stored.</summary>
    public static bool IsFromFile(string sourceFile, string expectedSource) =>
        sourceFile.Equals(expectedSource, StringComparison.Ordinal)
        || sourceFile.EndsWith($"/{expectedSource}", StringComparison.Ordinal);

    /// <summary>Collapses every whitespace run to a single space and trims, so line breaks, table padding
    /// and an overlay's re-indentation do not decide whether an answer span was retrieved.</summary>
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : $"{text[..57]}...";
}
