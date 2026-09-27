using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>What a note chunk order repair did: notes whose positions it moved, and notes whose text order it could not prove.</summary>
public sealed record NoteChunkOrderReport(int NotesReordered, int NotesUnproven);

/// <summary>
///     Puts the chunk positions of every multi-row note that cites a source file into the note's text order
///     (docs/adr/0122). Each note keeps the positions it already held; a note whose order
///     <see cref="NoteTextOrder" /> cannot prove is left as stored. Safe to run again: a note in text order moves nothing.
/// </summary>
public sealed class NoteChunkOrderRepair
{
    public async Task<NoteChunkOrderReport> RunAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
             SELECT id AS Id, path AS Path, value AS Value, chunk_index AS ChunkIndex,
                    {MemorySql.ContextKeyExpression("")} AS Ctx
             FROM entries
             WHERE source_file IS NOT NULL AND path IS NOT NULL AND path <> source_file AND value IS NOT NULL
             """, cancellationToken: cancellationToken));

        var reordered = 0;
        var unproven = 0;
        foreach (var note in rows.GroupBy(row => (row.Ctx, row.Path)).Where(group => group.Count() > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = NoteTextOrder.Find(note.Key.Path, [.. note.Select(row => new NoteRow(row.Id, row.Value, row.ChunkIndex))]);
            if (order is null)
            {
                unproven++;
                continue;
            }

            var moves = NoteTextOrder.Repositioned(order);
            if (moves.Count == 0)
            {
                continue;
            }

            await MoveAsync(connection, moves, cancellationToken);
            reordered++;
        }

        return new NoteChunkOrderReport(reordered, unproven);
    }

    /// <summary>Moves one note's rows in one transaction, so a note is never left holding a position twice.</summary>
    private static async Task MoveAsync(SqliteConnection connection, IReadOnlyList<(long Id, long ChunkIndex)> moves,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken));
        try
        {
            foreach (var (id, chunkIndex) in moves)
            {
                await connection.ExecuteAsync(new CommandDefinition("UPDATE entries SET chunk_index = @chunkIndex WHERE id = @id",
                    new { id, chunkIndex }, cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: cancellationToken));
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
            throw;
        }
    }

    // A plain class, not a record: Ctx is a computed column, which an empty result set reports as byte[] —
    // property-set materialization tolerates that; record constructor-matching does not.
    private sealed class Row
    {
        public long Id { get; set; }

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";

        public long ChunkIndex { get; set; }

        public string Ctx { get; set; } = "";
    }
}
