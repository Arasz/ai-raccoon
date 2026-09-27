using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory;

/// <summary>One stored row of a memory_write note: its id, its text, and its chunk_index (-1 when unknown).</summary>
public sealed record NoteRow(long Id, string Value, long ChunkIndex);

/// <summary>
///     Recovers the text order of a note's rows. A memory_write note's path names the SHA-256 of its whole body,
///     so an order counts only when its rows, with each overlay repeat dropped, join back into text with that hash.
/// </summary>
public static class NoteTextOrder
{
    private const int MaxJoinsPerOrder = 256;

    /// <summary>The path hashes the body as written, but rows hold it with \n endings; a body that used one other
    /// ending throughout is restored and checked too. Mixed endings cannot be restored without guessing.</summary>
    private static readonly string[] BodyLineEndings = ["\n", "\r\n", "\r"];

    /// <summary>The rows in text order, or null when no candidate order joins back into the body the path names.</summary>
    public static IReadOnlyList<NoteRow>? Find(string path, IReadOnlyList<NoteRow> rows)
    {
        Guard.IsNotNull(path);
        Guard.IsNotNull(rows);

        var bodyHash = Path.GetFileNameWithoutExtension(path);
        if (rows.Count == 0 || bodyHash.Length != 64)
        {
            return null;
        }

        foreach (var order in Candidates(rows))
        {
            var budget = MaxJoinsPerOrder;
            if (Joins(order, 1, new StringBuilder(order[0].Value), bodyHash, ref budget))
            {
                return order;
            }
        }

        return null;
    }

    /// <summary>
    ///     The position each row must take for positions to follow <paramref name="inTextOrder" />: the rows' own
    ///     positions, sorted and handed out in text order. Lists only rows that move; empty when any position is unknown.
    /// </summary>
    public static IReadOnlyList<(long Id, long ChunkIndex)> Repositioned(IReadOnlyList<NoteRow> inTextOrder)
    {
        Guard.IsNotNull(inTextOrder);

        if (inTextOrder.Any(row => row.ChunkIndex < 0))
        {
            return [];
        }

        var positions = inTextOrder.Select(row => row.ChunkIndex).Order().ToList();
        return
        [
            .. inTextOrder
                .Select((row, i) => (row.Id, ChunkIndex: positions[i], Moved: row.ChunkIndex != positions[i]))
                .Where(row => row.Moved)
                .Select(row => (row.Id, row.ChunkIndex))
        ];
    }

    /// <summary>Every rotation of the id order and of the position order. Writers append a note's rows as one run,
    /// in text order or with the opening last; a re-chunk keeps a run's place, so text order is one of these.</summary>
    private static IEnumerable<List<NoteRow>> Candidates(IReadOnlyList<NoteRow> rows)
    {
        List<NoteRow> byId = [.. rows.OrderBy(row => row.Id)];
        List<NoteRow>? byPosition = rows.All(row => row.ChunkIndex >= 0)
            ? [.. rows.OrderBy(row => row.ChunkIndex).ThenBy(row => row.Id)]
            : null;
        for (var shift = 0; shift < byId.Count; shift++)
        {
            yield return Rotated(byId, shift);
            if (byPosition is not null)
            {
                yield return Rotated(byPosition, shift);
            }
        }
    }

    /// <summary>The list with its last <paramref name="shift" /> items moved to the front.</summary>
    private static List<NoteRow> Rotated(List<NoteRow> rows, int shift) =>
        [.. rows.Skip(rows.Count - shift), .. rows.Take(rows.Count - shift)];

    private static bool Joins(List<NoteRow> order, int next, StringBuilder text, string bodyHash, ref int budget)
    {
        if (next == order.Count)
        {
            budget--;
            var joined = text.ToString();
            return BodyLineEndings.Any(ending => string.Equals(
                ContentHash.OfValue(ending == "\n" ? joined : joined.Replace("\n", ending, StringComparison.Ordinal)),
                bodyHash, StringComparison.Ordinal));
        }

        var before = order[next - 1].Value;
        var value = order[next].Value;
        for (var overlay = Math.Min(before.Length, value.Length - 1); overlay >= 0 && budget > 0; overlay--)
        {
            if (!before.AsSpan().EndsWith(value.AsSpan(0, overlay), StringComparison.Ordinal))
            {
                continue;
            }

            var length = text.Length;
            text.Append(value, overlay, value.Length - overlay);
            if (Joins(order, next + 1, text, bodyHash, ref budget))
            {
                return true;
            }

            text.Length = length;
        }

        return false;
    }
}
