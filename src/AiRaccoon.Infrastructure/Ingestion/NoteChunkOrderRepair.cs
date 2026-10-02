using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>What a note chunk order repair did: notes whose positions it moved, and notes whose text order it could not prove.</summary>
public sealed record NoteChunkOrderReport(int NotesReordered, int NotesUnproven);

/// <summary>
///     Puts the chunk positions of multi-row notes that cite a source file into each note's text order. Each note
///     keeps the positions it already held; a note whose order <see cref="NoteTextOrder" /> cannot prove is left as
///     stored. Safe to run again: a note in text order moves nothing.
/// </summary>
public sealed class NoteChunkOrderRepair
{
    private const int PathsPerQuery = 500;

    private static readonly string SelectNoteRows =
        $"""
         SELECT id AS Id, path AS Path, value AS Value, chunk_index AS ChunkIndex,
                {MemorySql.ContextKeyExpression("")} AS Ctx
         FROM entries
         WHERE source_file IS NOT NULL AND path IS NOT NULL AND path <> source_file AND value IS NOT NULL
         """;

    /// <summary>Repairs every source-citing note in the bank. Notes still at the -1 sentinel are first given a place
    /// by the bank-wide keeping-order renumber sync also runs (adding no rows, so file rows keep theirs), so
    /// <see cref="NoteTextOrder.Repositioned" /> can act on them; the full-table pass is accepted because this runs
    /// once per bank.</summary>
    public async Task<NoteChunkOrderReport> RunAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        var unpositioned = await UnpositionedNotePathsAsync(connection, cancellationToken);
        if (unpositioned.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.RecomputeChunkColumnsBankWideKeepingOrder,
                new { lastId = long.MaxValue }, cancellationToken: cancellationToken));
        }

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(SelectNoteRows, cancellationToken: cancellationToken));
        return await ReorderAsync(connection, rows, cancellationToken);
    }

    /// <summary>Repairs only the source-citing notes stored under <paramref name="paths" />.</summary>
    public async Task<NoteChunkOrderReport> RunAsync(SqliteConnection connection, IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);
        Guard.IsNotNull(paths);

        List<Row> rows = [];
        foreach (var batch in paths.Chunk(PathsPerQuery))
        {
            rows.AddRange(await connection.QueryAsync<Row>(new CommandDefinition(SelectNoteRows + " AND path IN @paths",
                new { paths = batch }, cancellationToken: cancellationToken)));
        }

        return await ReorderAsync(connection, rows, cancellationToken);
    }

    /// <summary>Paths of source-citing notes holding a row whose position is still unknown: rows a merge just added.</summary>
    public async Task<IReadOnlyList<string>> UnpositionedNotePathsAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        return
        [
            .. await connection.QueryAsync<string>(new CommandDefinition(
                """
                SELECT DISTINCT path FROM entries
                WHERE chunk_index < 0 AND source_file IS NOT NULL AND path IS NOT NULL AND path <> source_file
                """, cancellationToken: cancellationToken))
        ];
    }

    private static async Task<NoteChunkOrderReport> ReorderAsync(SqliteConnection connection, IEnumerable<Row> rows,
        CancellationToken cancellationToken)
    {
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
        await connection.InWriteTransactionAsync(async () =>
        {
            foreach (var (id, chunkIndex) in moves)
            {
                await connection.ExecuteAsync(new CommandDefinition("UPDATE entries SET chunk_index = @chunkIndex WHERE id = @id",
                    new { id, chunkIndex }, cancellationToken: cancellationToken));
            }
        }, cancellationToken);
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
