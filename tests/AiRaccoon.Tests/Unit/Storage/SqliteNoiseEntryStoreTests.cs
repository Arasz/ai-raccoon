using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Persistence contract for noise_entries (ADR-0029/ADR-0039): a rejected write records with its
///     policy name, the summary/list read path an operator uses to inspect the store, and the
///     retention purge. Against a real scratch bank, not a fake.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteNoiseEntryStoreTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-entry-store");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteNoiseEntryStore _store;

    public SqliteNoiseEntryStoreTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteNoiseEntryStore(_factory);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task RecordAsync_ThenSummarize_CountsTheEntryUnderItsPolicy()
    {
        var request = new MemoryWriteRequest("proj-1", "[IMPORTANT: Background process x completed normally]", SourceFile: "note.md");

        await _store.RecordAsync(request, "HermesBackgroundProcessLog", expiresAtUnixSeconds: 2_000, nowUnixSeconds: 1_000,
            TestContext.Current.CancellationToken);

        var summary = await _store.SummarizeAsync(TestContext.Current.CancellationToken);

        summary.TotalCount.ShouldBe(1);
        summary.CountByPolicy.ShouldContainKeyAndValue("HermesBackgroundProcessLog", 1);
    }

    [Fact]
    public async Task ListRecentAsync_ReturnsTheRecordedContentAndProject()
    {
        var request = new MemoryWriteRequest("proj-1", "[ASYNC DELEGATION BATCH COMPLETE]", SourceFile: "note.md");

        await _store.RecordAsync(request, "HermesBackgroundProcessLog", expiresAtUnixSeconds: 2_000, nowUnixSeconds: 1_000,
            TestContext.Current.CancellationToken);

        var entries = await _store.ListRecentAsync(limit: 10, TestContext.Current.CancellationToken);

        entries.Count.ShouldBe(1);
        entries[0].RequestContent.ShouldBe("[ASYNC DELEGATION BATCH COMPLETE]");
        entries[0].ProjectId.ShouldBe("proj-1");
        entries[0].SourceFile.ShouldBe("note.md");
        entries[0].DetectedByPolicy.ShouldBe("HermesBackgroundProcessLog");
    }

    [Fact]
    public async Task PurgeExpiredAsync_RemovesOnlyRowsPastTheCutoff()
    {
        await _store.RecordAsync(new MemoryWriteRequest("proj-1", "old noise"), "policy-a",
            expiresAtUnixSeconds: 1_000, nowUnixSeconds: 0, TestContext.Current.CancellationToken);
        await _store.RecordAsync(new MemoryWriteRequest("proj-1", "fresh noise"), "policy-a",
            expiresAtUnixSeconds: 5_000, nowUnixSeconds: 0, TestContext.Current.CancellationToken);

        var purged = await _store.PurgeExpiredAsync(nowUnixSeconds: 2_000, TestContext.Current.CancellationToken);

        purged.ShouldBe(1);
        var remaining = await _store.ListRecentAsync(limit: 10, TestContext.Current.CancellationToken);
        remaining.Count.ShouldBe(1);
        remaining[0].RequestContent.ShouldBe("fresh noise");
    }

    [Fact]
    public async Task PurgeExpiredAsync_NothingExpired_RemovesNothing()
    {
        await _store.RecordAsync(new MemoryWriteRequest("proj-1", "fresh noise"), "policy-a",
            expiresAtUnixSeconds: 5_000, nowUnixSeconds: 0, TestContext.Current.CancellationToken);

        var purged = await _store.PurgeExpiredAsync(nowUnixSeconds: 1_000, TestContext.Current.CancellationToken);

        purged.ShouldBe(0);
    }
}
