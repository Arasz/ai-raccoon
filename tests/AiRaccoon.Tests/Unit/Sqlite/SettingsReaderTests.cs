using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sqlite;

/// <summary>
///     The one connection-level settings read: a missing key is null, an empty or malformed value comes
///     back verbatim for the caller to interpret, and a read on a connection inside a write transaction
///     sees that transaction's uncommitted state.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SettingsReaderTests
{
    [Fact]
    public async Task MissingKey_ReturnsNull()
    {
        await using var connection = await OpenAsync();

        (await connection.ReadSettingAsync("absent", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task EmptyValue_ReturnsEmptyString()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("INSERT INTO settings(key, value) VALUES ('k', '')");

        (await connection.ReadSettingAsync("k", CancellationToken.None)).ShouldBe("");
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("  padded  ")]
    [InlineData("{broken json")]
    public async Task MalformedValue_ReturnsTheRawText(string stored)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("INSERT INTO settings(key, value) VALUES ('k', @stored)", new { stored });

        (await connection.ReadSettingAsync("k", CancellationToken.None)).ShouldBe(stored);
    }

    [Fact]
    public async Task InsideAWriteTransaction_SeesUncommittedState()
    {
        await using var connection = await OpenAsync();

        var seen = await connection.InWriteTransactionAsync(async () =>
        {
            await connection.ExecuteAsync("INSERT INTO settings(key, value) VALUES ('k', 'pending')");
            return await connection.ReadSettingAsync("k", CancellationToken.None);
        }, CancellationToken.None);

        seen.ShouldBe("pending");
    }

    [Fact]
    public async Task CancelledToken_Throws()
    {
        await using var connection = await OpenAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => connection.ReadSettingAsync("k", cts.Token));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await connection.ExecuteAsync("CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        return connection;
    }
}
