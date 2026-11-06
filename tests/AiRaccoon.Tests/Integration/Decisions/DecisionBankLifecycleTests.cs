using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionBankLifecycleTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("decisions-bank");
    private readonly string _backupRoot = TestData.CreateTempRoot("decisions-bank-backup");
    private SqliteConnectionFactory Factory(string? key = null, string? root = null) => new(
        new InfrastructureOptions { DataRoot = root ?? _root, Rid = "osx-arm64", Scope = InstallScope.User }, new FixtureKey(key));

    [RetryFact]
    public async Task Version_twenty_two_bank_migrates_without_losing_existing_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var connection = await Factory().OpenBankAsync(ct))
        {
            await connection.ExecuteAsync("INSERT INTO settings(key,value) VALUES('decision-migration-proof','kept')");
            foreach (var table in DecisionSchema.TableNames.Reverse())
            {
                await connection.ExecuteAsync($"DROP TABLE {table}");
            }

            await connection.ExecuteAsync("PRAGMA application_id=0; PRAGMA user_version=22;");
        }

        await using var migrated = await Factory().OpenBankAsync(ct);
        (await migrated.ExecuteScalarAsync<int>("PRAGMA user_version")).ShouldBe(23);
        (await migrated.ExecuteScalarAsync<string>("SELECT value FROM settings WHERE key='decision-migration-proof'")).ShouldBe("kept");
        var tables = (await migrated.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table'")).ToHashSet(StringComparer.Ordinal);
        DecisionSchema.TableNames.ShouldAllBe(table => tables.Contains(table));
    }

    [RetryFact]
    public async Task DecisionBank_EncryptedBackupAndRekeyKeepTheWholeGraph()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory("fixture-original-key");
        string graph;
        await using (var connection = await factory.OpenBankAsync(ct))
        {
            await connection.ExecuteAsync("INSERT INTO projects(id,name,created_at) VALUES('project','Project',1)");
            await DecisionGraphFixture.SeedAsync(factory, connection, "project", ct);
            graph = await DecisionGraphFixture.DumpGraphAsync(connection);
            await connection.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        SqliteConnection.ClearAllPools();
        var backup = Factory("fixture-original-key", _backupRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(backup.BankPath)!);
        File.Copy(factory.BankPath, backup.BankPath);

        await factory.RekeyBankAsync("fixture-replacement-key", ct);

        await using (var rekeyed = await Factory("fixture-replacement-key").OpenBankAsync(ct))
        {
            (await DecisionGraphFixture.DumpGraphAsync(rekeyed)).ShouldBe(graph);
            (await rekeyed.ExecuteScalarAsync<long>("SELECT count(*) FROM decision_fts WHERE decision_fts MATCH 'kept'")).ShouldBe(1);
        }

        await using (var restored = await backup.OpenBankAsync(ct))
        {
            (await DecisionGraphFixture.DumpGraphAsync(restored)).ShouldBe(graph);
        }

        SqliteConnection.ClearAllPools();
        await Should.ThrowAsync<BankKeyMissingException>(() => Factory().OpenBankWithKeyAsync(null, ct));
        await Should.ThrowAsync<BankKeyMismatchException>(() => Factory("fixture-original-key").OpenBankAsync(ct));
        await Should.ThrowAsync<BankKeyMissingException>(() => Factory(null, _backupRoot).OpenBankWithKeyAsync(null, ct));
    }

    private sealed class FixtureKey(string? passphrase) : IEncryptionKeyResolver
    {
        public Task<ResolvedKey> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ResolvedKey(passphrase, "env"));
    }

    public void Dispose()
    {
        TestData.DeleteTempRoot(_root);
        TestData.DeleteTempRoot(_backupRoot);
    }
}
