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

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionBankLifecycleTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("decisions-bank");
    private SqliteConnectionFactory Factory(string? key = null) => new(
        new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User }, new FixtureKey(key));

    [Fact]
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
        (await migrated.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='decision_versions'")).ShouldBe(1);
    }

    [Fact]
    public async Task Encrypted_decision_graph_survives_rekey_and_refuses_plaintext_open()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory("fixture-original-key");
        var store = new SqliteDecisionStore(factory, new SharedProjectScope());
        await store.WriteAsync("project", async (writer, token) =>
        {
            var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
            await writer.AddTaskAsync(new DecisionTask("task", "Encrypted decision task", 1, now, null), token);
            await writer.AddRecordAsync(new DecisionRecord("decision", "task", 1, now), token);
            await writer.AddVersionAsync(new DecisionVersion("decision", 1, "task", 1,
                new DecisionContent("Encrypted title", "architecture", "scenario", "outcome", "reason"),
                DecisionStatus.Accepted, now, "actor", "session", null), token);
            return true;
        }, ct);
        await factory.RekeyBankAsync("fixture-replacement-key", ct);
        var reopened = new SqliteDecisionStore(Factory("fixture-replacement-key"), new SharedProjectScope());
        var versions = await reopened.ReadAsync("project", (reader, token) => reader.VersionsAsync("decision", token), ct);
        versions.Single().Content.Title.ShouldBe("Encrypted title");
        await Should.ThrowAsync<BankKeyMissingException>(() => Factory().OpenBankWithKeyAsync(null, ct));
    }

    private sealed class FixtureKey(string? passphrase) : IEncryptionKeyResolver
    {
        public Task<ResolvedKey> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ResolvedKey(passphrase, "env"));
    }

    public void Dispose() => TestData.DeleteTempRoot(_root);
}
