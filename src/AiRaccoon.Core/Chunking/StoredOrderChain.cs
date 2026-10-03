using System.Buffers;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Chunking;

/// <summary>One stored row of a file, in stored order: its text, and whether the current chunker reproduces it by hash.</summary>
public sealed record StoredOrderRow(long Id, string Value, bool Reproduced);

/// <summary>
///     Locates a file's stored rows in the file's text and keeps the rows whose text follows their stored order
///     (docs/adr/0123). Pure.
/// </summary>
public static class StoredOrderChain
{
    /// <summary>
    ///     The unreproduced rows to set to unknown: those with no place in the heaviest chain of rows whose text occurs
    ///     at strictly increasing offsets in stored order — text that left the file, moved, or survives only out of
    ///     place. A reproduced row outweighs every unreproduced row together, so the chain bends around the rows the
    ///     file proves by hash; those are never returned.
    /// </summary>
    public static IReadOnlySet<long> Unplaceable(IReadOnlyList<StoredOrderRow> rows, string content)
    {
        Guard.IsNotNull(rows);
        Guard.IsNotNull(content);

        var values = rows.Select(row => TextLines.NormalizeLineEndings(row.Value)).ToArray();
        var occurrences = Occurrences(TextLines.NormalizeLineEndings(content), values.Where(value => value.Length > 0));
        var heavy = rows.Count + 1;

        // Max-weight chain of (row, offset) nodes, offsets strictly increasing along stored order. Each row's
        // offsets are visited high to low, so a row never chains onto itself.
        var slots = occurrences.Values.SelectMany(list => list).Distinct().Order().ToArray();
        var tree = new (long Weight, int Node)[slots.Length + 1];
        Array.Fill(tree, (0L, -1));
        List<(int Row, int Previous)> nodes = [];
        for (var i = 0; i < rows.Count; i++)
        {
            if (!occurrences.TryGetValue(values[i], out var offsets))
            {
                continue;
            }

            var weight = rows[i].Reproduced ? heavy : 1;
            for (var k = offsets.Count - 1; k >= 0; k--)
            {
                var slot = Array.BinarySearch(slots, offsets[k]) + 1;
                var (best, previous) = Query(tree, slot - 1);
                nodes.Add((i, previous));
                Update(tree, slot, (best + weight, nodes.Count - 1));
            }
        }

        var kept = new HashSet<long>();
        for (var node = Query(tree, slots.Length).Node; node >= 0; node = nodes[node].Previous)
        {
            kept.Add(rows[nodes[node].Row].Id);
        }

        return rows.Where(row => !row.Reproduced && !kept.Contains(row.Id)).Select(row => row.Id).ToHashSet();
    }

    /// <summary>Every offset at which each value occurs in <paramref name="text" />, overlaps included, found in one
    /// vectorized pass over the text rather than one pass per value.</summary>
    private static Dictionary<string, List<int>> Occurrences(string text, IEnumerable<string> values)
    {
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        var found = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        if (distinct.Length == 0)
        {
            return found;
        }

        var byFirst = distinct.GroupBy(value => value[0]).ToDictionary(group => group.Key, group => group.ToArray());
        var search = SearchValues.Create(distinct, StringComparison.Ordinal);
        var start = 0;
        while (start < text.Length)
        {
            var hit = text.AsSpan(start).IndexOfAny(search);
            if (hit < 0)
            {
                break;
            }

            var at = start + hit;
            foreach (var value in byFirst[text[at]])
            {
                if (text.AsSpan(at).StartsWith(value, StringComparison.Ordinal))
                {
                    if (!found.TryGetValue(value, out var offsets))
                    {
                        found[value] = offsets = [];
                    }

                    offsets.Add(at);
                }
            }

            start = at + 1;
        }

        return found;
    }

    /// <summary>The heaviest chain ending at a slot at or below <paramref name="slot" />; node -1 when there is none.</summary>
    private static (long Weight, int Node) Query((long Weight, int Node)[] tree, int slot)
    {
        (long Weight, int Node) best = (0, -1);
        for (; slot > 0; slot -= slot & -slot)
        {
            if (tree[slot].Weight > best.Weight)
            {
                best = tree[slot];
            }
        }

        return best;
    }

    private static void Update((long Weight, int Node)[] tree, int slot, (long Weight, int Node) value)
    {
        for (; slot < tree.Length; slot += slot & -slot)
        {
            if (value.Weight > tree[slot].Weight)
            {
                tree[slot] = value;
            }
        }
    }
}
