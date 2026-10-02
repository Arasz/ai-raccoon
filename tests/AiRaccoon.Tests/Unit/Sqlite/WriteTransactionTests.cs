using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sqlite;

/// <summary>
///     The one BEGIN IMMEDIATE / COMMIT / ROLLBACK wrapper every bank writer shares: work commits
///     on success, rolls back on failure or cancellation, and never leaves the connection inside
///     an open transaction.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WriteTransactionTests
{
    [Fact]
    public async Task Success_CommitsTheWork()
    {
        await using var connection = await OpenAsync();

        await connection.InWriteTransactionAsync(
            () => connection.ExecuteAsync("INSERT INTO t(v) VALUES (1)"), CancellationToken.None);

        (await CountAsync(connection)).ShouldBe(1);
        connection.ShouldNotBeInTransaction();
    }

    [Fact]
    public async Task Success_ReturnsTheWorkResult()
    {
        await using var connection = await OpenAsync();

        var inserted = await connection.InWriteTransactionAsync(
            () => connection.ExecuteAsync("INSERT INTO t(v) VALUES (1), (2)"), CancellationToken.None);

        inserted.ShouldBe(2);
    }

    [Fact]
    public async Task Failure_RollsBackAndRethrowsTheOriginalException()
    {
        await using var connection = await OpenAsync();

        var thrown = await Should.ThrowAsync<InvalidOperationException>(() =>
            connection.InWriteTransactionAsync(async () =>
            {
                await connection.ExecuteAsync("INSERT INTO t(v) VALUES (1)");
                throw new InvalidOperationException("boom");
            }, CancellationToken.None));

        thrown.Message.ShouldBe("boom");
        (await CountAsync(connection)).ShouldBe(0);
        connection.ShouldNotBeInTransaction();
    }

    [Fact]
    public async Task CancelledMidWork_StillRollsBack()
    {
        await using var connection = await OpenAsync();
        using var cts = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            connection.InWriteTransactionAsync(async () =>
            {
                await connection.ExecuteAsync("INSERT INTO t(v) VALUES (1)");
                await cts.CancelAsync();
                cts.Token.ThrowIfCancellationRequested();
            }, cts.Token));

        (await CountAsync(connection)).ShouldBe(0);
        connection.ShouldNotBeInTransaction();
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE TABLE t(v INTEGER)");
        return connection;
    }

    private static Task<long> CountAsync(SqliteConnection connection) =>
        connection.ExecuteScalarAsync<long>("SELECT count(*) FROM t");
}

internal static class TransactionAssertions
{
    /// <summary>Asserts no transaction is open: SQLite rejects a nested BEGIN, so a fresh one must succeed.</summary>
    public static void ShouldNotBeInTransaction(this SqliteConnection connection) =>
        Should.NotThrow(() =>
        {
            connection.Execute("BEGIN TRANSACTION");
            connection.Execute("ROLLBACK TRANSACTION");
        });
}
