using System.Text.Json;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncServiceTests
{
    [Fact]
    public async Task MemorySync_WithoutCredentials_ThrowsSyncNotConfigured()
    {
        var service = new SyncService(new SyncOptions(), new FakeConnectionFactory(new FakeConnection([])));

        await Should.ThrowAsync<SyncNotConfiguredException>(() =>
            service.MemorySyncAsync("acme", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MemorySync_WithCredentials_EnablesCommittedContextsAndRunsSequence()
    {
        var connection = new FakeConnection(["shared", "project:acme"]);
        var service = new SyncService(
            new SyncOptions { ManagedDatabaseId = "db-1", ApiKey = "test-key-0000" },
            new FakeConnectionFactory(connection));

        var result = await service.MemorySyncAsync("acme", TestContext.Current.CancellationToken);

        result.ShouldBe(new SyncResult(3, 5, 7));
        connection.Calls.ShouldBe(new[]
        {
            "committed",
            "enable:shared",
            "enable:project:acme",
            "init:db-1",
            "apikey:test-key-0000",
            "sync",
            "sync",
            "reindex"
        });
    }

    [Fact]
    public async Task MemorySync_EnablesEveryDistinctProjectContextInTheBank()
    {
        var connection = new FakeConnection(["shared", "project:acme", "project:other-app"]);
        var service = new SyncService(
            new SyncOptions { ManagedDatabaseId = "db-1", ApiKey = "test-key-0000" },
            new FakeConnectionFactory(connection));

        await service.MemorySyncAsync("acme", TestContext.Current.CancellationToken);

        connection.Calls.Where(c => c.StartsWith("enable:", StringComparison.Ordinal))
            .ShouldBe(["enable:shared", "enable:project:acme", "enable:project:other-app"]);
    }

    [Fact]
    public async Task MemorySync_EnablesExactlyTheCommittedContextsReportedByTheConnection()
    {
        var connection = new FakeConnection(["shared", "project:acme"]);
        var service = new SyncService(
            new SyncOptions { ManagedDatabaseId = "db-1", ApiKey = "test-key-0000" },
            new FakeConnectionFactory(connection));

        await service.MemorySyncAsync("acme", TestContext.Current.CancellationToken);

        connection.Calls.Where(c => c.StartsWith("enable:", StringComparison.Ordinal))
            .ShouldBe(["enable:shared", "enable:project:acme"]);
    }

    [Fact]
    public async Task MemorySync_UsesSentFromFirstAndReceivedFromSecondSyncCall()
    {
        var connection = new FakeConnection(["shared", "project:acme"]);
        var service = new SyncService(
            new SyncOptions { ManagedDatabaseId = "db-1", ApiKey = "test-key-0000" },
            new FakeConnectionFactory(connection));

        var result = await service.MemorySyncAsync("acme", TestContext.Current.CancellationToken);

        result.Sent.ShouldBe(3);
        result.Received.ShouldBe(5);
    }

    [Fact]
    public void Parse_ReadsSendAndReceiveCounts() =>
        CloudSyncCounts.Parse("""{"send":{"ok":true,"count":3},"receive":{"ok":true,"count":5}}""")
            .ShouldBe(new CloudSyncCounts(3, 5));

    [Fact]
    public void Parse_MissingCountDefaultsToZero() =>
        CloudSyncCounts.Parse("""{"send":{"ok":false},"receive":{"ok":true,"count":2}}""")
            .ShouldBe(new CloudSyncCounts(0, 2));

    [Fact]
    public void Parse_InvalidJson_ThrowsJsonException() => Should.Throw<JsonException>(() => CloudSyncCounts.Parse("not json"));

    private sealed class FakeConnection(IReadOnlyList<string> committedContexts) : ICloudSyncConnection
    {
        public List<string> Calls { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IReadOnlyList<string>> GetCommittedContextsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("committed");
            return Task.FromResult(committedContexts);
        }

        public Task EnableSyncAsync(string context, CancellationToken cancellationToken)
        {
            Calls.Add($"enable:{context}");
            return Task.CompletedTask;
        }

        public Task NetworkInitAsync(string databaseId, CancellationToken cancellationToken)
        {
            Calls.Add($"init:{databaseId}");
            return Task.CompletedTask;
        }

        public Task SetApiKeyAsync(string apiKey, CancellationToken cancellationToken)
        {
            Calls.Add($"apikey:{apiKey}");
            return Task.CompletedTask;
        }

        public Task<CloudSyncCounts> NetworkSyncAsync(CancellationToken cancellationToken)
        {
            Calls.Add("sync");
            return Task.FromResult(new CloudSyncCounts(3, 5));
        }

        public Task<int> ReindexAsync(CancellationToken cancellationToken)
        {
            Calls.Add("reindex");
            return Task.FromResult(7);
        }
    }

    private sealed class FakeConnectionFactory(FakeConnection connection) : ICloudSyncConnectionFactory
    {
        public Task<ICloudSyncConnection> OpenAsync(CancellationToken cancellationToken) => Task.FromResult<ICloudSyncConnection>(connection);
    }
}
