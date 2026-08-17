using System.Text;
using AiRaccoon.Core.Chunking;

namespace AiRaccoon.Tests.Integration.Retrieval;

/// <summary>
///     The two tuning arms ADR-0077 did not reject, built here rather than in production so they can
///     be scored before anything ships. Both re-shape only table regions and delegate everything else
///     to the real <see cref="MarkdownChunker" />, so a score difference is the arm's and not a
///     side effect of a second chunker.
/// </summary>
internal static class TableChunkingArms
{
    /// <summary>One chunk per body row, each carrying the header — the shape ADR-0077 said the
    /// evidence points at, expressing "for this column and this row, the value is X".</summary>
    public static IMarkdownChunker PerRow(TokenCount count) => new TableRewritingChunker(count, RenderRows, false);

    /// <summary>One chunk per body row, rendered as sentences instead of pipe syntax — ADR-0077's
    /// most promising arm, since an embedding model handles a sentence better than `| a | b |`.</summary>
    public static IMarkdownChunker Linearised(TokenCount count) => new TableRewritingChunker(count, RenderSentences, false);

    /// <summary>The whole table as one chunk, prefixed with the section heading it sits under. A table
    /// carrying no `#` line of its own gets a null section and forfeits the 4x bm25 section weight
    /// (FileIngestor.HeadingSection parses the heading out of the chunk text) — this puts one back.</summary>
    public static IMarkdownChunker WholeTableWithHeading(TokenCount count) =>
        new TableRewritingChunker(count, RenderWholeTable, true);

    /// <summary>Per-row with the section heading restored.</summary>
    public static IMarkdownChunker PerRowWithHeading(TokenCount count) =>
        new TableRewritingChunker(count, RenderRows, true);

    /// <summary>Linearised rows with the section heading restored.</summary>
    public static IMarkdownChunker LinearisedWithHeading(TokenCount count) =>
        new TableRewritingChunker(count, RenderSentences, true);

    private static IReadOnlyList<string> RenderWholeTable(IReadOnlyList<string> header, IReadOnlyList<string> rows) =>
        [string.Concat(header[0], header[1], string.Concat(rows))];

    private static IReadOnlyList<string> RenderRows(IReadOnlyList<string> header, IReadOnlyList<string> rows) =>
        [.. rows.Select(row => string.Concat(header[0], header[1], row))];

    /// <summary>"Bucket: clipped. What it means: the stored text hit the cap." — one sentence per cell,
    /// with the heading text kept so the column name is still searchable.</summary>
    private static IReadOnlyList<string> RenderSentences(IReadOnlyList<string> header, IReadOnlyList<string> rows)
    {
        var columns = Cells(header[0]);
        return
        [
            .. rows.Select(row =>
            {
                var cells = Cells(row);
                var sentence = new StringBuilder();
                for (var i = 0; i < cells.Count; i++)
                {
                    var name = i < columns.Count ? columns[i] : $"column {i + 1}";
                    if (cells[i].Length == 0)
                    {
                        continue;
                    }

                    sentence.Append(name).Append(": ").Append(cells[i]);
                    sentence.Append(cells[i].EndsWith('.') ? " " : ". ");
                }

                return sentence.ToString().TrimEnd() + "\n";
            })
        ];
    }

    private static List<string> Cells(string line) =>
        [.. line.Trim().Trim('|').Split('|').Select(cell => cell.Trim())];

    /// <summary>
    ///     Splits table regions with a supplied renderer and hands every other line to the real
    ///     chunker, so prose chunking is identical across arms. A rendered piece that still exceeds
    ///     the budget falls back to the real chunker for that region.
    /// </summary>
    private sealed class TableRewritingChunker(
        TokenCount count,
        Func<IReadOnlyList<string>, IReadOnlyList<string>, IReadOnlyList<string>> render,
        bool withHeading) : IMarkdownChunker
    {
        private readonly MarkdownChunker _inner = new(count);

        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null)
        {
            var counter = countTokens ?? count;
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            List<string> chunks = [];
            var prose = new StringBuilder();
            var inFence = false;
            var heading = string.Empty;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsFence(line))
                {
                    inFence = !inFence;
                }

                if (!inFence && line.TrimStart().StartsWith('#'))
                {
                    heading = WithNewline(line.Trim());
                }

                if (!inFence && StartsTable(lines, i))
                {
                    Flush(prose, chunks, maxTokens, overlayTokens, counter);
                    var end = i + 2;
                    while (end < lines.Length && IsPipeRow(lines[end]))
                    {
                        end++;
                    }

                    IReadOnlyList<string> header = [WithNewline(lines[i]), WithNewline(lines[i + 1])];
                    var rows = lines[(i + 2)..end].Select(WithNewline).ToList();
                    var prefix = withHeading && heading.Length > 0 ? heading : string.Empty;
                    foreach (var rendered in render(header, rows))
                    {
                        var piece = prefix + rendered;
                        chunks.AddRange(counter(piece) <= maxTokens ? [piece] : _inner.Chunk(piece, maxTokens, 0, counter));
                    }

                    i = end - 1;
                    continue;
                }

                prose.Append(line).Append('\n');
            }

            Flush(prose, chunks, maxTokens, overlayTokens, counter);
            return chunks;
        }

        private void Flush(StringBuilder prose, List<string> chunks, int maxTokens, int overlayTokens, TokenCount counter)
        {
            if (prose.Length == 0)
            {
                return;
            }

            var text = prose.ToString();
            prose.Clear();
            if (text.Trim().Length > 0)
            {
                chunks.AddRange(_inner.Chunk(text, maxTokens, overlayTokens, counter));
            }
        }

        private static bool IsFence(string line) =>
            line.TrimStart().StartsWith("```", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal);

        private static bool IsPipeRow(string line) => line.TrimStart().StartsWith('|');

        private static bool StartsTable(string[] lines, int index) =>
            IsPipeRow(lines[index]) && index + 1 < lines.Length && IsSeparatorRow(lines[index + 1]);

        private static bool IsSeparatorRow(string line)
        {
            if (!IsPipeRow(line))
            {
                return false;
            }

            var dashes = 0;
            foreach (var character in line.Trim())
            {
                if (character == '-')
                {
                    dashes++;
                }
                else if (character is not ('|' or ':' or ' ' or '\t'))
                {
                    return false;
                }
            }

            return dashes > 0;
        }

        private static string WithNewline(string line) => line.EndsWith('\n') ? line : line + "\n";
    }
}
