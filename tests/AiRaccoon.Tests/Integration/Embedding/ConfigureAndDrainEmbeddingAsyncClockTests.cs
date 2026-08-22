using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     D4 quality-gate finding: <see cref="TestData.ConfigureAndDrainEmbeddingAsync" /> built its
///     drain-time <c>EntryEmbedder</c>/<c>SqliteModelMigrationLease</c> from <c>TimeProvider.System</c>
///     while the fixture that called it runs a <see cref="FakeTimeProvider" /> — a latent flake, since
///     any assertion on a migration's stamped timestamp would silently compare against real wall-clock
///     time instead of the fixture's simulated clock. The helper must use the caller's clock when one
///     is supplied.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ConfigureAndDrainEmbeddingAsyncClockTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-configure-drain-clock");
    private FakeTimeProvider _clock = null!;
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), _clock,
            TestData.CreateEmbeddingService());
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ConfigureAndDrainEmbeddingAsync_WithASuppliedClock_StampsTheMigrationFromThatClock_NotRealTime()
    {
        var ct = TestContext.Current.CancellationToken;

        // First configure: fresh bank, no previous engine — writes settings only, no migration row.
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _factory, TestData.CreateEmbeddingService(),
            "local", null, null, ct, _clock);
        await _store.WriteAsync(new MemoryWriteRequest("acme", "seed row"), ct);

        // Second configure: a different local model directory changes the engine fingerprint,
        // opening and draining a real migration — the path that actually stamps started_at/finished_at.
        var otherPath = Path.Combine(Path.GetTempPath(), "ai-raccoon-clock-test-model",
            Guid.NewGuid().ToString("N"), BundledModel.ModelFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(otherPath)!);
        File.Copy(BundledModel.ResolveModelPath(), otherPath);
        try
        {
            await TestData.ConfigureAndDrainEmbeddingAsync(_store, _factory, TestData.CreateEmbeddingService(),
                "local", otherPath, null, ct, _clock);
        }
        finally
        {
            TestData.DeleteTempRoot(Path.GetDirectoryName(otherPath)!);
        }

        // started_at comes from the STORE's own injected clock (already correct — SqliteMemoryStore
        // passes its own TimeProvider into StartModelMigrationAsync). finished_at is stamped by the
        // DRAIN's EntryEmbedder, the exact clock this helper builds fresh — the one this test targets.
        var finishedAt = await ReadFinishedAtAsync();
        finishedAt.ShouldBe(FixedNow.ToUnixTimeSeconds(),
            "the drain must stamp finished_at from the caller's supplied clock, not real wall-clock time");
    }

    private async Task<long?> ReadFinishedAtAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<long?>(new CommandDefinition(
            "SELECT finished_at FROM model_migration WHERE id = 1",
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
