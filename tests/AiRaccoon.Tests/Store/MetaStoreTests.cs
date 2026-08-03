using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MetaStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly MetaStore _store;

    public MetaStoreTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            loadExtensions: _ => { });
        _store = new MetaStore(_factory, new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task UpsertAccess_TimestampsComeFromInjectedClock()
    {
        var entry = await _store.UpsertAccessAsync(
            "acme", "abc123", cancellationToken: TestContext.Current.CancellationToken);

        entry.CreatedAt.ShouldBe(FixedNow.ToUnixTimeSeconds());
        entry.LastAccessedAt.ShouldBe(FixedNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task UpsertAccess_OnMissingEntry_CreatesRowWithProvenance()
    {
        var entry = await _store.UpsertAccessAsync(
            "acme", "abc123", "agent-1", "project:acme",
            TestContext.Current.CancellationToken);

        entry.AccessCount.ShouldBe(1);
        entry.AgentId.ShouldBe("agent-1");
        entry.Context.ShouldBe("project:acme");
        entry.Rating.ShouldBe(
            RatingPolicy.Rating(RatingPolicy.DefaultBaseScore, 1, 0, RatingPolicy.DefaultHalfLifeDays));
        entry.LastAccessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task UpsertAccess_WithSharedContext_StoresAndReturnsSharedRow()
    {
        var entry = await _store.UpsertAccessAsync(
            "acme", "abc123", context: "shared", cancellationToken: TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("shared");
        (await _store.GetEntryAsync("acme", "abc123", TestContext.Current.CancellationToken))!.Context
            .ShouldBe("shared");
    }

    [Fact]
    public async Task UpsertAccess_OnExistingEntry_IncrementsCountAndRaisesRating()
    {
        await _store.UpsertAccessAsync("acme", "abc123", cancellationToken: TestContext.Current.CancellationToken);
        var second =
            await _store.UpsertAccessAsync("acme", "abc123", cancellationToken: TestContext.Current.CancellationToken);

        second.AccessCount.ShouldBe(2);
        second.Rating.ShouldBeGreaterThan(RatingPolicy.DefaultBaseScore);
    }

    [Fact]
    public async Task GetEntry_ReturnsNull_WhenMissing()
    {
        var entry = await _store.GetEntryAsync("acme", "nope", TestContext.Current.CancellationToken);

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task GetEntry_ReturnsStoredRow()
    {
        await _store.UpsertAccessAsync("acme", "abc123", "agent-1", "project:acme",
            TestContext.Current.CancellationToken);

        var entry = await _store.GetEntryAsync("acme", "abc123", TestContext.Current.CancellationToken);

        entry.ShouldNotBeNull();
        entry.Hash.ShouldBe("abc123");
        entry.ProjectId.ShouldBe("acme");
        entry.Rating.ShouldBe(
            RatingPolicy.Rating(RatingPolicy.DefaultBaseScore, 1, 0, RatingPolicy.DefaultHalfLifeDays));
    }

    [Fact]
    public async Task ListEntries_ReturnsAllRowsForProject_NewestFirst()
    {
        await _store.UpsertAccessAsync("acme", "h1", context: "project:acme",
            cancellationToken: TestContext.Current.CancellationToken);
        await _store.UpsertAccessAsync("acme", "h2", context: "project:acme",
            cancellationToken: TestContext.Current.CancellationToken);

        var entries = await _store.ListEntriesAsync("acme", TestContext.Current.CancellationToken);

        entries.Count.ShouldBe(2);
        entries.Select(e => e.Hash).ShouldBe(["h2", "h1"]);
    }

    [Fact]
    public async Task Delete_RemovesEntry_AndReturnsTrue()
    {
        await _store.UpsertAccessAsync("acme", "abc123", context: "project:acme",
            cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteAsync("acme", "abc123", TestContext.Current.CancellationToken);

        deleted.ShouldBeTrue();
        (await _store.GetEntryAsync("acme", "abc123", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await _store.ListEntriesAsync("acme", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_OnMissingEntry_ReturnsFalse()
    {
        var deleted = await _store.DeleteAsync("acme", "nope", TestContext.Current.CancellationToken);

        deleted.ShouldBeFalse();
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-meta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
