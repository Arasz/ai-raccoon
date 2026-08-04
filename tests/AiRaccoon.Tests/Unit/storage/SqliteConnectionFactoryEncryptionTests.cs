using System.Data;
using System.Text;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteConnectionFactoryEncryptionTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private SqliteConnectionFactory Factory(string? passphrase = null) =>
        new(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            new StubEncryptionKeyProvider(passphrase));

    [Fact]
    public async Task OpenBankAsync_WithPassphrase_CreatesEncryptedDatabase()
    {
        var passphrase = "test-encryption-key";
        var factory = Factory(passphrase);

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(ConnectionState.Open);
        File.Exists(factory.BankPath).ShouldBeTrue();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await connection.CloseAsync();

        // Verify encryption: file header is not the standard SQLite header
        var header = File.ReadAllBytes(factory.BankPath);
        var headerStr = Encoding.ASCII.GetString(header[..16]);
        headerStr.ShouldNotStartWith("SQLite format 3");

        // Verify the DB can be reopened with the correct passphrase
        await using var reopen = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task OpenBankAsync_WithoutPassphrase_OpensUnencryptedDatabase()
    {
        var factory = Factory(passphrase: null);

        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(ConnectionState.Open);
        File.Exists(factory.BankPath).ShouldBeTrue();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await connection.CloseAsync();

        // Verify unencrypted: standard header
        var header = File.ReadAllBytes(factory.BankPath);
        var headerStr = Encoding.ASCII.GetString(header[..16]);
        headerStr.ShouldStartWith("SQLite format 3");

        // Reopening without passphrase must succeed
        await using var reopen = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        reopen.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task OpenBankAsync_WithWrongPassphrase_FailsToOpen()
    {
        var passphrase = "correct-passphrase";
        var factory = Factory(passphrase);

        await using (var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, value TEXT)";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var wrongFactory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            new StubEncryptionKeyProvider("wrong-passphrase"));

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var conn = await wrongFactory.OpenBankAsync(TestContext.Current.CancellationToken);
        });
        ex.SqliteErrorCode.ShouldBe(26);
    }

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("airaccoon-store-tests");

    private sealed class StubEncryptionKeyProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string? GetPassphrase() => passphrase;
    }
}
