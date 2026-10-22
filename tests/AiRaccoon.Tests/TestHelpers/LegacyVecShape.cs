using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A bank older than the typed vector layout: its vector tables carry no kind partition, and
///     its embed triggers place a vector without one. The typed trigger body inserts the row's kind
///     as the second partition key, so a fixture that simulates such a bank has to restore the old
///     bodies along with the old tables — exactly as a real old bank has them. Without this the
///     fixture fails at its first embedded row, with an error about a column its table does not
///     have.
/// </summary>
public static class LegacyVecShape
{
    public static async Task RestoreUntypedTriggersAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             DROP TRIGGER IF EXISTS vec_entries_au;
             DROP TRIGGER IF EXISTS vec_structure_au;

             CREATE TRIGGER vec_entries_au AFTER UPDATE OF embed_state ON entries
             WHEN NEW.embed_state = 'embedded' AND NEW.embedding IS NOT NULL
             BEGIN
                 DELETE FROM vec_entries WHERE rowid = NEW.id;
                 INSERT INTO vec_entries(rowid, ctx, embedding) VALUES (NEW.id, {MemorySql.ContextKeyExpression("NEW.")}, NEW.embedding);
             END;

             CREATE TRIGGER vec_structure_au AFTER UPDATE OF structure_embedding ON entries
             WHEN NEW.structure_embedding IS NOT NULL
             BEGIN
                 DELETE FROM vec_structure WHERE rowid = NEW.id;
                 INSERT INTO vec_structure(rowid, ctx, embedding) VALUES (NEW.id, {MemorySql.ContextKeyExpression("NEW.")}, NEW.structure_embedding);
             END;
             """, cancellationToken: cancellationToken));
    }
}
