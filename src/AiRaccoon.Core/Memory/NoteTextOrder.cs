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
    /// <summary>A note with more rows than this is not searched; it is reported unprovable.</summary>
    internal const int MaxRows = 1024;

    /// <summary>Rotations tried of each base order: a writer's opening-last layout needs one, a re-chunked head a few.</summary>
    internal const int MaxShifts = 4;

    /// <summary>Work one <see cref="Find(string, IReadOnlyList{NoteRow})" /> call may spend: one unit per overlay
    /// tried at a junction, one per row for each joined body hashed. Past it the note is unprovable.</summary>
    internal const int MaxWork = 100_000;

    /// <summary>The path hashes the body as written, but rows hold it with \n endings; a body that used one other
    /// ending throughout is restored and checked too. Mixed endings cannot be restored without guessing.</summary>
    private static readonly string[] BodyLineEndings = ["\n", "\r\n", "\r"];

    /// <summary>The rows in text order, or null when no candidate order joins back into the body the path names
    /// within the work budget.</summary>
    public static IReadOnlyList<NoteRow>? Find(string path, IReadOnlyList<NoteRow> rows) => Find(path, rows, out _);

    internal static IReadOnlyList<NoteRow>? Find(string path, IReadOnlyList<NoteRow> rows, out int work)
    {
        Guard.IsNotNull(path);
        Guard.IsNotNull(rows);

        work = 0;
        var bodyHash = Path.GetFileNameWithoutExtension(path);
        if (rows.Count == 0 || rows.Count > MaxRows || bodyHash.Length != 64)
        {
            return null;
        }

        var candidates = Candidates(rows).ToList();
        var share = MaxWork / candidates.Count;
        foreach (var order in candidates)
        {
            if (Joins(order, bodyHash, ref work, work + share))
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

    /// <summary>The id order and the position order, each with up to <see cref="MaxShifts" /> of its last rows moved to
    /// the front. Writers append a note's rows as one run, in text order or with the opening last; a re-chunk keeps a
    /// run's place and its pieces at the run's slot.</summary>
    private static IEnumerable<List<NoteRow>> Candidates(IReadOnlyList<NoteRow> rows)
    {
        List<NoteRow> byId = [.. rows.OrderBy(row => row.Id)];
        List<NoteRow>? byPosition = rows.All(row => row.ChunkIndex >= 0)
            ? [.. rows.OrderBy(row => row.ChunkIndex).ThenBy(row => row.Id)]
            : null;
        for (var shift = 0; shift < Math.Min(byId.Count, MaxShifts + 1); shift++)
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

    /// <summary>Searches the overlay choices at every junction depth first, with an explicit stack so a long note
    /// cannot exhaust the thread's stack, and gives up once <paramref name="work" /> would pass <paramref name="limit" />:
    /// each candidate order gets an equal share, so a wrong one cannot starve the rest.</summary>
    private static bool Joins(List<NoteRow> order, string bodyHash, ref int work, int limit)
    {
        var text = new StringBuilder(order[0].Value);
        var overlays = new List<int>?[order.Count];
        var tried = new int[order.Count];
        var lengths = new int[order.Count];
        var junction = 1;
        while (work < limit)
        {
            if (junction == order.Count)
            {
                if (work + order.Count > limit)
                {
                    return false;
                }

                work += order.Count;
                if (Matches(text.ToString(), bodyHash))
                {
                    return true;
                }

                junction--;
                if (junction == 0)
                {
                    return false;
                }

                continue;
            }

            if (overlays[junction] is null)
            {
                overlays[junction] = Overlays(order[junction - 1].Value, order[junction].Value, ref work, limit);
                tried[junction] = 0;
                lengths[junction] = text.Length;
            }

            var choices = overlays[junction]!;
            if (tried[junction] == choices.Count)
            {
                overlays[junction] = null;
                junction--;
                if (junction == 0)
                {
                    return false;
                }

                continue;
            }

            var overlay = choices[tried[junction]++];
            var value = order[junction].Value;
            text.Length = lengths[junction];
            text.Append(value, overlay, value.Length - overlay);
            junction++;
        }

        return false;
    }

    /// <summary>Overlay lengths the next row may start with, longest first: whole lines that open it and also close the
    /// row before, or none. The chunker's overlay is whole units, and a unit that starts mid-line (a later piece of a cut
    /// line) never shares a row with the piece before it.</summary>
    private static List<int> Overlays(string before, string value, ref int work, int limit)
    {
        List<int> overlays = [];
        var longest = Math.Min(before.Length, value.Length - 1);
        for (var end = value.LastIndexOf('\n', Math.Max(longest - 1, 0));
             end >= 0 && end < longest && work < limit;
             end = end == 0 ? -1 : value.LastIndexOf('\n', end - 1))
        {
            work++;
            var overlay = end + 1;
            var startsALine = overlay == before.Length || before[before.Length - overlay - 1] == '\n';
            if (startsALine && before.AsSpan().EndsWith(value.AsSpan(0, overlay), StringComparison.Ordinal))
            {
                overlays.Add(overlay);
            }
        }

        overlays.Add(0);
        return overlays;
    }

    private static bool Matches(string joined, string bodyHash) =>
        BodyLineEndings.Any(ending => string.Equals(
            ContentHash.OfValue(ending == "\n" ? joined : joined.Replace("\n", ending, StringComparison.Ordinal)),
            bodyHash, StringComparison.Ordinal));
}
