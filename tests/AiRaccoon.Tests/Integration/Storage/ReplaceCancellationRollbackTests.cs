using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     A replace cancelled after <c>BEGIN IMMEDIATE</c> must still ROLLBACK: a pooled connection
///     handed back inside its transaction fails the next borrower ("cannot start a transaction
///     within a transaction") and starves other writers with SQLITE_BUSY. The replace reads the
///     clock right after each BEGIN, so a clock that cancels on its Nth read lands the cancellation
///     inside the claim transaction or the main transaction; every N must leave the pool clean.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ReplaceCancellationRollbackTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-replace-cancel-rollback");
    private readonly SqliteConnectionFactory _factory;

    public ReplaceCancellationRollbackTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task ReplaceIfFileChangedAsync_CancelledOnTheNthClockRead_LeavesNoConnectionInATransaction(
        int cancelOnRead)
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var time = new CancellingTimeProvider(FixedNow, cancelOnRead, cts);
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), time,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        var file = Path.Combine(_dataRoot, "cancelled.md");
        await File.WriteAllTextAsync(file, "content for the cancelled-replace test", ct);
        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        try
        {
            await store.ReplaceIfFileChangedAsync("acme", file, "fresh-hash", cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected whenever the cancellation lands before the replace completes.
        }

        await using var next = await _factory.OpenBankAsync(ct);
        await Should.NotThrowAsync(async () =>
        {
            await next.ExecuteAsync("BEGIN IMMEDIATE");
            await next.ExecuteAsync("ROLLBACK");
        }, "the next borrower must be able to start a transaction");
    }

    private sealed class CancellingTimeProvider(DateTimeOffset now, int cancelOnRead, CancellationTokenSource cts)
        : FakeTimeProvider(now)
    {
        private int _reads;

        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _reads) == cancelOnRead)
            {
                cts.Cancel();
            }

            return base.GetTimestamp();
        }
    }
}
