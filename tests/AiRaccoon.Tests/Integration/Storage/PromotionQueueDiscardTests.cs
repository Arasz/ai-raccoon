using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.Integration.Storage;

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
    private readonly FakeTimeProvider _clock;
    private readonly string _contentRoot = TestData.CreateTempRoot("ai-raccoon-queue-discard-content");

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-queue-discard");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqlitePromotionQueueStore _queueStore;
    private readonly SqliteMemoryStore _store;

    public PromotionQueueDiscardTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        _store = TestData.CreateMemoryStore(_factory,
            NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), _clock, TestData.CreateEmbeddingService());
        _queueStore = new SqlitePromotionQueueStore(_factory, _clock);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _dataRoot, _contentRoot })
        {
            TestData.DeleteTempRoot(root);
        }
    }

    private static QueueCandidate Candidate(string hash, string value, double score) => new(hash, $"{hash}.md", value, null, score, ["organic-write"]);

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
            new MemoryWriteRequest("acme", "discarded fact"),
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
            new MemoryWriteRequest("acme", "durable shared fact"),
            TestContext.Current.CancellationToken);
        // Pre-fix residue order: the twin is queued FIRST, then the content gets shared.
        await _queueStore.UpsertAsync("acme", [Candidate("fresh-hash-1", "durable shared fact", 2.0)],
            TestContext.Current.CancellationToken);
        await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
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

    /// <summary>G5 pin: PromoteAsync claims rows through the store's DiscardAsync but must never
    /// remember those claims as rejections — a promotion is not a "no".</summary>
    [Fact]
    public async Task Promote_DoesNotRememberClaimsAsDiscards()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "promotable fact"),
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate(entry.Hash, "promotable fact", 2.0)],
            TestContext.Current.CancellationToken);

        var outcome = await CreateService().PromoteAsync(["acme"], 20, TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldContain(entry.Hash);
        (await DiscardCountAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(0,
            "a promotion claim must not be recorded as a discard");
    }

    /// <summary>G6 pin: capacity eviction removes queue rows without remembering them — only an
    /// agent's explicit discard is a rejection.</summary>
    [Fact]
    public async Task Eviction_DoesNotRememberVictimsAsDiscards()
    {
        await _store.SetSettingAsync(ExtractionConfigKeys.QueueCapacityGlobal, "3",
            TestContext.Current.CancellationToken);
        var service = CreateService();

        var outcome = await service.ProposeAsync("acme",
            [
                Candidate("h-1", "fact one", 1.0), Candidate("h-2", "fact two", 1.0),
                Candidate("h-3", "fact three", 1.0), Candidate("h-4", "fact four", 1.0)
            ],
            TestContext.Current.CancellationToken);

        outcome.Evicted.Count.ShouldBe(1);
        (await DiscardCountAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(0,
            "capacity eviction must not be recorded as a rejection");
    }

    /// <summary>G8: the tool-equivalent round trip — discard via the service, propose again — the
    /// hash stays out of the queue and the rejection is persisted.</summary>
    [Fact]
    public async Task DiscardThenPropose_DoesNotReQueueTheHash()
    {
        var service = CreateService();
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "rejected fact"),
            TestContext.Current.CancellationToken);
        await service.ProposeAsync("acme", [Candidate(entry.Hash, "rejected fact", 1.0)],
            TestContext.Current.CancellationToken);
        await service.DiscardAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        await service.ProposeAsync("acme", [Candidate(entry.Hash, "rejected fact", 1.0)],
            TestContext.Current.CancellationToken);

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldNotContain(entry.Hash, "a discarded hash must not be re-queued");
        (await DiscardCountAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    /// <summary>G9: the replace round-trip (watch re-ingest) must not restore a discarded
    /// candidate whose unchanged chunk returns under the same hash — the mirror of
    /// PromotionQueueInvalidationTests.ReplacingAWatchedFile_KeepsCandidatesForUnchangedChunks.</summary>
    [Fact]
    public async Task ReplaceFile_DoesNotRestoreDiscardedCandidate()
    {
        await SetScopeAsync("acme");
        var path = Path.Combine(_contentRoot, "adr.md");
        await File.WriteAllTextAsync(path, "stable adr content", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", path, null, TestContext.Current.CancellationToken);
        var hash = await SingleEntryHashAsync("acme", path);
        await _queueStore.UpsertAsync("acme", [Candidate(hash, "stable adr content", 1.0)],
            TestContext.Current.CancellationToken);
        await _queueStore.RememberDiscardsAsync("acme", [hash], TestContext.Current.CancellationToken);

        // Unchanged content: the chunk returns under the same hash after the replace.
        await File.WriteAllTextAsync(path, "stable adr content", TestContext.Current.CancellationToken);
        await _store.ReplaceIfFileChangedAsync("acme", path, "file-hash-2", TestContext.Current.CancellationToken);

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldNotContain(hash,
                "a discarded candidate must not return through the replace round-trip");
    }

    /// <summary>
    ///     ADR-0026's requirement — the table reaches existing banks with no schema-version bump —
    ///     survives ADR-0075: adding it changes <c>Ddl</c>, which changes the digest, which forces the
    ///     rerun. What no longer heals is a digest-matched bank missing the table, which is manual
    ///     surgery or corruption, not a version skew. Both halves are asserted so the narrowing is a
    ///     recorded decision rather than a silent regression.
    /// </summary>
    [Fact]
    public async Task PromotionDiscards_HealsOnADigestSkewedBank_ButNotOnADigestMatchedOne()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition("DROP TABLE IF EXISTS promotion_discards",
                cancellationToken: TestContext.Current.CancellationToken));
        }

        // Digest still matches, so Ddl is skipped and the table stays missing. This is the property
        // ADR-0075 trades away for a 39-statement-to-4 fast path on every bank open.
        await using (var stillMissing = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            (await stillMissing.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'promotion_discards'",
                    cancellationToken: TestContext.Current.CancellationToken)))
                .ShouldBe(0, "a digest-matched bank skips Ddl — see ADR-0075");
        }

        // Any real delivery path — a build whose Ddl gained the table, or a pre-digest bank — reads a
        // different application_id, and the heal ADR-0026 depends on still happens.
        await using (var skewed = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await skewed.ExecuteAsync(new CommandDefinition("PRAGMA application_id = 0",
                cancellationToken: TestContext.Current.CancellationToken));
        }

        await using var reopened = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await reopened.ExecuteAsync(new CommandDefinition(
            "INSERT INTO promotion_discards (project_id, hash, discarded_at) VALUES ('acme', 'h', 0)",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private PromotionQueueService CreateService() =>
        new(_queueStore, _store, new UniformCountEvictionPolicy(), new NoopMetrics(),
            NullLogger<PromotionQueueService>.Instance, _clock);

    private async Task<long> DiscardCountAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "SELECT count(*) FROM promotion_discards WHERE project_id = @ProjectId",
                    new { ProjectId = projectId },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<string> SingleEntryHashAsync(string projectId, string sourceFile)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(
            "SELECT hash FROM entries WHERE project_id = @projectId AND source_file = @sourceFile",
            new { projectId, sourceFile });
    }

    private Task SetScopeAsync(string projectId) =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeProject(projectId), IngestScopeKeys.Serialize([_contentRoot]),
            TestContext.Current.CancellationToken);

    private sealed class NoopMetrics : IPromotionQueueMetrics
    {
        public void RecordEviction(string projectId, double victimScore, string reason)
        {
        }

        public void RecordPromoted(string projectId, double waitSeconds)
        {
        }

        public void RecordDiscarded(string projectId, double waitSeconds)
        {
        }

        public void RecordPruned(string projectId, int count)
        {
        }

        public void RecordFailed(string projectId, int count)
        {
        }

        public void RecordSnapshot(PromotionQueueStats stats, int capacity)
        {
        }
    }

}
