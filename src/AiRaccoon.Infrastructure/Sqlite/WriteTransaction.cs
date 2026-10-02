using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Runs bank writes inside one BEGIN IMMEDIATE transaction: writers queue on the write lock instead of failing with SQLITE_BUSY mid-work.</summary>
public static class WriteTransaction
{
    /// <summary>Runs <paramref name="work" /> in a BEGIN IMMEDIATE transaction; commits on success, rolls back and rethrows on failure or cancellation.</summary>
    public static async Task<T> InWriteTransactionAsync<T>(this SqliteConnection connection, Func<Task<T>> work,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken));
        try
        {
            var result = await work();
            await connection.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: cancellationToken));
            return result;
        }
        catch
        {
            // Uncancellable: a cancelled token would skip the ROLLBACK and leave the transaction open.
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
            throw;
        }
    }

    /// <inheritdoc cref="InWriteTransactionAsync{T}" />
    public static Task InWriteTransactionAsync(this SqliteConnection connection, Func<Task> work,
        CancellationToken cancellationToken) =>
        connection.InWriteTransactionAsync(async () =>
        {
            await work();
            return true;
        }, cancellationToken);
}
