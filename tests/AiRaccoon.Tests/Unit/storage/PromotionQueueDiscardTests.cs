using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

/// <summary>
///     Persistent discards + shared-exclusion (docs/adr/0026): a discarded (project_id, hash) is
///     remembered so propose never re-queues it, and PruneRejectedAsync sweeps pre-fix residue —
///     already-shared values and discarded hashes — off the queue.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PromotionQueueDiscardTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-queue-discard");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _clock;
    private readonly SqliteMemoryStore _store;
    private readonly SqlitePromotionQueueStore _queueStore;

    public PromotionQueueDiscardTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        _store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(),
            NullLogger<SqliteMemoryStore>.Instance);
        _queueStore = new SqlitePromotionQueueStore(_factory, _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, true);
        }
    }

    private static QueueCandidate Candidate(string hash, string value, double score) =>
        new(hash, $"{hash}.md", value, null, score, ["organic-write"]);

    private async Task<long> SharedRowCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT count(*) FROM entries WHERE scope = 'shared'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>G3: after a discard is remembered, an upsert of the same hash is refused — the
    /// queue never holds content an agent explicitly rejected.</summary>
    [Fact]
    public async Task DiscardedHash_IsNotReQueuedByUpsert()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "discarded fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate(entry.Hash, "discarded fact", 1.0)],
            TestContext.Current.CancellationToken);
        await _queueStore.DiscardAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        await _queueStore.RememberDiscardsAsync("acme", [entry.Hash], TestContext.Current.CancellationToken);

        var added = await _queueStore.UpsertAsync("acme", [Candidate(entry.Hash, "discarded fact", 1.0)],
            TestContext.Current.CancellationToken);

        added.ShouldBe(0);
        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken)).ShouldBeEmpty(
            "a discarded hash must not be re-queued");
    }

    /// <summary>G4: PruneRejectedAsync sweeps pre-fix residue — a queued row whose value is
    /// already in the shared tier, and a queued row whose hash was discarded — while leaving the
    /// shared row itself untouched.</summary>
    [Fact]
    public async Task PruneRejectedAsync_RemovesSharedTwinAndDiscardedResidue()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "durable shared fact", null, null, null, null, null),
            TestContext.Current.CancellationToken);
        await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate("fresh-hash-1", "durable shared fact", 2.0)],
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate("fresh-hash-2", "rejected fact", 1.5)],
            TestContext.Current.CancellationToken);
        await _queueStore.RememberDiscardsAsync("acme", ["fresh-hash-2"], TestContext.Current.CancellationToken);

        var removed = await _queueStore.PruneRejectedAsync("acme", TestContext.Current.CancellationToken);

        removed.ShouldBe(2);
        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await SharedRowCountAsync(TestContext.Current.CancellationToken)).ShouldBe(1,
            "pruning the queue must never touch the shared tier");
    }

    /// <summary>G7: a refused (discarded) upsert is not counted as a genuinely new row — the
    /// propose outcome stays honest.</summary>
    [Fact]
    public async Task Upsert_RefusedDiscardedHash_IsNotCountedAsNew()
    {
        await _queueStore.RememberDiscardsAsync("acme", ["dead-hash"], TestContext.Current.CancellationToken);

        var added = await _queueStore.UpsertAsync("acme",
            [Candidate("dead-hash", "rejected", 1.0), Candidate("fresh-hash", "fresh", 1.0)],
            TestContext.Current.CancellationToken);

        added.ShouldBe(1);
        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe(["fresh-hash"]);
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
