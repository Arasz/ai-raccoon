using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>
///     Line-range code splitter (docs/work/2026-08-21-code-search-implementation-plan.md §3.4, no
///     AST in v1): the primary unit is a blank-line block; packing prefers to end a chunk at the
///     last brace-balance-0 boundary reachable within the token budget, else the budget wins. A
///     unit too big to fit alone falls back to per-line units, and a single line too big to fit
///     alone is hard-split via <see cref="TokenBudget" />'s binary search — the same idiom
///     <see cref="EmbeddingService.TrimQueryToWindow" /> uses. Packing is exact-joined-recount
///     (docs/adr/0036, mirroring <see cref="MarkdownChunker" />'s shape); overlay is always 0 —
///     code chunks carry line ranges, not prose continuity. Counts with <see cref="ICodeTokenizer" />
///     — the bundled code-daemon-embed-v1 tokenizer when no code engine is configured (plan §3.3).
/// </summary>
public sealed class CodeChunker : ICodeChunker
{
    /// <summary>v1 default budget: min(510, ctx − reservation) for code-daemon-embed-v1's 128-token
    /// context and 2-token &lt;s&gt;/&lt;/s&gt; reservation (plan §12.1 H3) — 126, never the flat 510
    /// the engine plan's narrative text describes, and never the memory chunker's 254.</summary>
    public const int DefaultBudget = 126;

    private readonly TokenCount _countTokens;
    private readonly int _budget;

    public CodeChunker(ICodeTokenizer tokenizer, int budget = DefaultBudget)
    {
        Guard.IsNotNull(tokenizer);
        Guard.IsGreaterThan(budget, 0);
        _countTokens = tokenizer.CountTokens;
        _budget = budget;
    }

    public IReadOnlyList<CodeChunk> Chunk(string text)
    {
        Guard.IsNotNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var lines = SplitLines(NormalizeLineEndings(text));
        if (lines.Count == 0 || lines.All(IsBlank))
        {
            return [];
        }

        var units = BuildUnits(lines, _countTokens, _budget);
        return BuildChunks(units, _budget, _countTokens);
    }

    private static List<CodeChunk> BuildChunks(List<Unit> units, int maxTokens, TokenCount countTokens)
    {
        List<CodeChunk> chunks = [];
        var cursor = 0;
        while (cursor < units.Count)
        {
            var boundary = ChooseBoundary(units, cursor, maxTokens);
            var end = VerifyAndShed(units, cursor, boundary, maxTokens, countTokens);
            var text = string.Concat(units.Skip(cursor).Take(end - cursor + 1).Select(u => u.Text));
            chunks.Add(new CodeChunk(text, units[cursor].LineStart, units[end].LineEnd));
            cursor = end + 1;
        }

        return chunks;
    }

    /// <summary>
    ///     Greedy budget-max reach from cursor, then prefer the LAST brace-balance-0 boundary within
    ///     that reach; falls back to the greedy max when no such boundary exists ("budget wins").
    /// </summary>
    private static int ChooseBoundary(List<Unit> units, int cursor, int maxTokens)
    {
        var tokens = 0;
        var maxReach = cursor;
        var c = cursor;
        while (c < units.Count)
        {
            var candidate = tokens + units[c].TokenCount;
            if (c > cursor && candidate > maxTokens)
            {
                break;
            }

            tokens = candidate;
            maxReach = c;
            c++;
        }

        for (var k = maxReach; k >= cursor; k--)
        {
            if (units[k].EndBalance == 0)
            {
                return k;
            }
        }

        return maxReach;
    }

    /// <summary>
    ///     Verifies the joined text of units[cursor..end] against the real tokenizer and sheds
    ///     trailing units if the summed estimate drifted over budget (non-composable token counts,
    ///     docs/adr/0036) — mirrors <see cref="MarkdownChunker" />'s shed loop, minus overlay. At
    ///     least one unit always survives: every unit is individually proven &lt;= maxTokens when built.
    /// </summary>
    private static int VerifyAndShed(List<Unit> units, int cursor, int end, int maxTokens, TokenCount countTokens)
    {
        while (end > cursor)
        {
            var joined = string.Concat(units.Skip(cursor).Take(end - cursor + 1).Select(u => u.Text));
            if (countTokens(joined) <= maxTokens)
            {
                return end;
            }

            end--;
        }

        return cursor;
    }

    /// <summary>
    ///     Groups lines into blank-line-block units: a maximal run of non-blank lines plus its
    ///     trailing blank-line run (or leading blanks, merged into the first real block). A block
    ///     that fits the budget alone is one atomic unit; an oversized block falls back to per-line
    ///     units via <see cref="AddLineOrSplit" />.
    /// </summary>
    private static List<Unit> BuildUnits(List<string> lines, TokenCount countTokens, int maxTokens)
    {
        List<Unit> units = [];
        var balance = 0;
        List<string> block = [];
        var blockStart = 1;

        for (var i = 0; i < lines.Count; i++)
        {
            var lineNumber = i + 1;
            block.Add(lines[i]);

            var atTransition = IsBlank(lines[i]) && i + 1 < lines.Count && !IsBlank(lines[i + 1])
                                && block.Any(l => !IsBlank(l));
            if (!atTransition)
            {
                continue;
            }

            balance = AddBlock(units, block, blockStart, lineNumber, countTokens, maxTokens, balance);
            block = [];
            blockStart = lineNumber + 1;
        }

        if (block.Count > 0)
        {
            AddBlock(units, block, blockStart, lines.Count, countTokens, maxTokens, balance);
        }

        return units;
    }

    private static int AddBlock(List<Unit> units, List<string> block, int lineStart, int lineEnd,
        TokenCount countTokens, int maxTokens, int balance)
    {
        var joined = string.Concat(block);
        var count = countTokens(joined);
        if (count <= maxTokens)
        {
            var endBalance = balance + BraceDelta(joined);
            units.Add(new Unit(joined, count, lineStart, lineEnd, endBalance));
            return endBalance;
        }

        for (var i = 0; i < block.Count; i++)
        {
            balance = AddLineOrSplit(units, block[i], lineStart + i, countTokens, maxTokens, balance);
        }

        return balance;
    }

    private static int AddLineOrSplit(List<Unit> units, string line, int lineNumber, TokenCount countTokens,
        int maxTokens, int balance)
    {
        var count = countTokens(line);
        if (count <= maxTokens)
        {
            var endBalance = balance + BraceDelta(line);
            units.Add(new Unit(line, count, lineNumber, lineNumber, endBalance));
            return endBalance;
        }

        var remaining = line;
        while (remaining.Length > 0)
        {
            var head = TokenBudget.Trim(remaining, maxTokens, countTokens);
            if (head.Length == 0)
            {
                // Even a single character tokenizes over budget; take it anyway so every split
                // makes forward progress and the loop is guaranteed to terminate.
                head = remaining[..1];
            }

            balance += BraceDelta(head);
            units.Add(new Unit(head, countTokens(head), lineNumber, lineNumber, balance));
            remaining = remaining[head.Length..];
        }

        return balance;
    }

    private static int BraceDelta(string text)
    {
        var delta = 0;
        foreach (var ch in text)
        {
            if (ch == '{')
            {
                delta++;
            }
            else if (ch == '}')
            {
                delta--;
            }
        }

        return delta;
    }

    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

    private static List<string> SplitLines(string text)
    {
        List<string> lines = [];
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>One packable unit: its own line span and the cumulative file-wide brace balance
    /// right after it ends (a crude, non-AST heuristic — braces inside strings/comments count too).</summary>
    private sealed record Unit(string Text, int TokenCount, int LineStart, int LineEnd, int EndBalance);
}
