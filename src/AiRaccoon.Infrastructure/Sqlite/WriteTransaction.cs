using Dapper;
using System.Data;
using SQLitePCL;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Serializes bank writes with BEGIN IMMEDIATE and contains nested writes in savepoints.</summary>
public static class WriteTransaction
{
    extension(SqliteConnection connection)
    {
        /// <summary>Commits on success or rolls back on failure; nested calls preserve the outer transaction.</summary>
        public async Task<T> InWriteTransactionAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
        {
            var nested = connection.State == ConnectionState.Open && raw.sqlite3_get_autocommit(connection.Handle) == 0;
            var begin = nested ? "SAVEPOINT ai_raccoon_write;" : "BEGIN IMMEDIATE;";
            var commit = nested ? "RELEASE SAVEPOINT ai_raccoon_write;" : "COMMIT;";
            var rollback = nested ? "ROLLBACK TO SAVEPOINT ai_raccoon_write; RELEASE SAVEPOINT ai_raccoon_write;" : "ROLLBACK;";
            await connection.ExecuteAsync(new CommandDefinition(begin, cancellationToken: cancellationToken));
            try
            {
                var result = await work();
                await connection.ExecuteAsync(new CommandDefinition(commit, cancellationToken: cancellationToken));
                return result;
            }
            catch
            {
                // Uncancellable: a cancelled token would skip the ROLLBACK and leave the transaction open.
                await connection.ExecuteAsync(new CommandDefinition(rollback, cancellationToken: CancellationToken.None));
                throw;
            }
        }

        /// <inheritdoc cref="InWriteTransactionAsync{T}" />
        public Task InWriteTransactionAsync(Func<Task> work, CancellationToken cancellationToken) =>
            connection.InWriteTransactionAsync(async () =>
            {
                await work();
                return true;
            }, cancellationToken);
    }
}
