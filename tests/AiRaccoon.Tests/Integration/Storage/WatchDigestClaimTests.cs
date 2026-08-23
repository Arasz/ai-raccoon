using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP12 review round 2: two properties of the <c>watch_digest_claims</c> row
///     <see cref="SqliteMemoryStore.ReplaceCoreAsync" /> claims before chunking.
///     <para>
///         Claim ownership is tracked explicitly (<c>ownsClaim</c>), not re-derived from
///         "a guard was passed" — a failure BEFORE this call actually won the claim (the unlocked
///         guard, or the claim transaction itself throwing) must never delete a claim some OTHER
///         call is holding.
///     </para>
///     <para>The crash-timeout reclaim is the only self-healing path for a claim whose holder
///     crashed mid-chunk and never released it — pinned here with a <see cref="FakeTimeProvider" />
///     so it needs no real waiting.</para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WatchDigestClaimTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-watch-digest-claim");
    private readonly SqliteConnectionFactory _factory;

    public WatchDigestClaimTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ReplaceIfFileChangedAsync_WhenTheGuardThrowsBeforeClaiming_LeavesAForeignClaimUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(FixedNow);
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), time, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        var file = Path.Combine(_dataRoot, "guard-throws.md");
        await File.WriteAllTextAsync(file, "content for the guard-throws test", ct);
        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        // A foreign, fresh claim already sitting on this exact path — as if another replace is
        // mid-chunk right now.
        await SeedClaimAsync("acme", file, time.GetUtcNow().ToUnixTimeSeconds(), ct);

        // Force the unlocked guard's own read (SelectWatchFile) to throw, before this call ever
        // reaches the claim step.
        await using (var breaker = await _factory.OpenBankAsync(ct))
        {
            await breaker.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE watch_files RENAME TO watch_files_hidden_for_test", cancellationToken: ct));
        }

        try
        {
            await Should.ThrowAsync<SqliteException>(() =>
                store.ReplaceIfFileChangedAsync("acme", file, "fresh-hash", ct));
        }
        finally
        {
            await using var restore = await _factory.OpenBankAsync(ct);
            await restore.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE watch_files_hidden_for_test RENAME TO watch_files", cancellationToken: ct));
        }

        (await ClaimCountAsync("acme", file, ct)).ShouldBe(1,
            "a claim this call never won must not be deleted by its own failure cleanup");
    }

    [Fact]
    public async Task ReplaceIfFileChangedAsync_ClaimOlderThanStaleAfter_IsReclaimedAndChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(FixedNow);
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), time, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        var file = Path.Combine(_dataRoot, "stale-claim.md");
        await File.WriteAllTextAsync(file, "content for the stale-claim reclaim test", ct);
        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        var staleClaimedAt = time.GetUtcNow().Subtract(SqliteMemoryStore.ClaimStaleAfter).AddSeconds(-1)
            .ToUnixTimeSeconds();
        await SeedClaimAsync("acme", file, staleClaimedAt, ct);

        var result = await store.ReplaceIfFileChangedAsync("acme", file, "fresh-hash", ct);

        result.Replaced.ShouldBeTrue("a claim older than ClaimStaleAfter must be reclaimed, not honored");
        (await EntryCountAsync("acme", file, ct)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReplaceIfFileChangedAsync_ClaimFresherThanStaleAfter_Declines()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(FixedNow);
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), time, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        var file = Path.Combine(_dataRoot, "fresh-claim.md");
        await File.WriteAllTextAsync(file, "content for the fresh-claim decline test", ct);
        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        var freshClaimedAt = time.GetUtcNow().Subtract(SqliteMemoryStore.ClaimStaleAfter).AddSeconds(1)
            .ToUnixTimeSeconds();
        await SeedClaimAsync("acme", file, freshClaimedAt, ct);

        var result = await store.ReplaceIfFileChangedAsync("acme", file, "fresh-hash", ct);

        result.Replaced.ShouldBeFalse("a claim still inside ClaimStaleAfter must not be reclaimed");
        (await EntryCountAsync("acme", file, ct)).ShouldBe(0,
            "the file must not have been chunked while a fresh foreign claim holds it");
    }

    private async Task SeedClaimAsync(string projectId, string path, long claimedAt, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watch_digest_claims (project_id, path, claimed_at) VALUES (@projectId, @path, @claimedAt)",
            new { projectId, path, claimedAt }, cancellationToken: ct));
    }

    private async Task<long> ClaimCountAsync(string projectId, string path, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM watch_digest_claims WHERE project_id = @projectId AND path = @path",
            new { projectId, path }, cancellationToken: ct));
    }

    private async Task<long> EntryCountAsync(string projectId, string path, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND source_file = @path",
            new { projectId, path }, cancellationToken: ct));
    }
}
