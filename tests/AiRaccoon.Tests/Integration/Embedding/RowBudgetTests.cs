using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP11-C (docs/work/2026-08-22-post-delta-3-plan.md §WP11, owner gates G18/G19): the embed
///     drain's rows-per-run becomes one bank setting, <c>maintenance.embed-rows-per-run.global</c>,
///     read by <see cref="EmbedDrainService" /> for both corpora — default 128
///     (<see cref="BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun" />), unchanged from B2's
///     hardcoded constant.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RowBudgetTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("row-budget");
    private readonly SqliteConnectionFactory _factory;

    public RowBudgetTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>C1: a configured rows-per-run bounds one drain pass exactly, for the memory corpus.</summary>
    [Fact]
    public async Task RowsPerRun_ComesFromTheBankSetting()
    {
        await ConfigureMemoryProviderAsync();
        await SeedPendingMemoryRowsAsync(20);
        await SetRowsPerRunAsync("7");
        var pump = TestData.NewEmbedDrainPump();
        var service = NewService(pump);

        pump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory));
        await service.DrainOnceAsync(new EmbedDrainRequest(EmbedCorpus.Memory), TestContext.Current.CancellationToken);

        (await EmbeddedMemoryCountAsync()).ShouldBe(7);
        (await PendingMemoryCountAsync()).ShouldBe(13);
    }

    /// <summary>C2: the same setting, and the same consumer, also bounds a code-corpus drain pass.</summary>
    [Fact]
    public async Task CodeReindex_HonoursTheSameSetting()
    {
        await ActivateCodeEngineAsync();
        await SeedPendingCodeRowsAsync(20);
        await SetRowsPerRunAsync("7");
        var pump = TestData.NewEmbedDrainPump();
        var service = NewService(pump);

        await service.DrainOnceAsync(new EmbedDrainRequest(EmbedCorpus.Code), TestContext.Current.CancellationToken);

        (await EmbeddedCodeCountAsync()).ShouldBe(7);
        (await PendingCodeCountAsync()).ShouldBe(13);
    }

    [Fact]
    public void ResolveRowsPerRun_Unset_Returns128_AndNeverWarns()
    {
        var logger = new FakeLogger<EmbedDrainService>();
        var service = NewService(TestData.NewEmbedDrainPump(), logger: logger);

        service.ResolveRowsPerRun(null).ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);

        logger.Collector.GetSnapshot().ShouldBeEmpty("an unset setting is not garbage — nothing to warn about");
    }

    /// <summary>A present-but-unparseable value falls back to the default and warns — asserted on the logger's structured record, not its text.</summary>
    [Fact]
    public void ResolveRowsPerRun_Garbage_Returns128_AndWarns()
    {
        var logger = new FakeLogger<EmbedDrainService>();
        var service = NewService(TestData.NewEmbedDrainPump(), logger: logger);

        service.ResolveRowsPerRun("not-a-number").ShouldBe(BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun);

        var records = logger.Collector.GetSnapshot();
        records.Count(r => r.Level == LogLevel.Warning).ShouldBe(1);
    }

    /// <summary>
    ///     Review finding 2 (#517): ResolveRowsPerRun runs on every drain pass (every ~15s
    ///     on-demand poll, at least), so a persistent bad value used to warn forever. Two passes
    ///     over the SAME bad raw value must log exactly once.
    /// </summary>
    [Fact]
    public void ResolveRowsPerRun_CalledTwiceWithTheSameGarbage_WarnsOnlyOnce()
    {
        var logger = new FakeLogger<EmbedDrainService>();
        var service = NewService(TestData.NewEmbedDrainPump(), logger: logger);

        service.ResolveRowsPerRun("not-a-number");
        service.ResolveRowsPerRun("not-a-number");

        logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning).ShouldBe(1);
    }

    /// <summary>A DIFFERENT bad value is a fresh occurrence — worth its own warning, not swallowed by the first one's memory.</summary>
    [Fact]
    public void ResolveRowsPerRun_TwoDistinctGarbageValues_WarnsForEach()
    {
        var logger = new FakeLogger<EmbedDrainService>();
        var service = NewService(TestData.NewEmbedDrainPump(), logger: logger);

        service.ResolveRowsPerRun("garbage-one");
        service.ResolveRowsPerRun("garbage-two");

        logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning).ShouldBe(2);
    }

    /// <summary>Review finding 1: the parser clamps an over-ceiling value to the ceiling AND logs — same invalid-value warning path as garbage.</summary>
    [Fact]
    public void ResolveRowsPerRun_OverCeiling_ClampsToTheCeiling_AndWarns()
    {
        var logger = new FakeLogger<EmbedDrainService>();
        var service = NewService(TestData.NewEmbedDrainPump(), logger: logger);

        service.ResolveRowsPerRun("2000000000").ShouldBe(BankMaintenanceConfigKeys.MaxEmbedRowsPerRun);

        logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning).ShouldBe(1);
    }

    private EmbedDrainService NewService(IEventPump<EmbedDrainRequest> pump, ILogger<EmbedDrainService>? logger = null) =>
        new(pump, _factory,
            new EntryEmbedder(new CountingEmbeddingService(), Substitute.For<IModelMigrationLease>(), TimeProvider.System, new VecDimensionReconciler()),
            new CodeEmbedder(new FakeCodeEmbeddingService(), NullLoggerFor<CodeEmbedder>(), new VecDimensionReconciler()),
            new SqliteSettingsStore(_factory), NoOpMeasurementRecorder.Instance, TimeProvider.System,
            TestTelemetry.None, logger ?? NullLoggerFor<EmbedDrainService>());

    private static ILogger<T> NullLoggerFor<T>() => Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    private async Task SetRowsPerRunAsync(string value)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal, value },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task ConfigureMemoryProviderAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.Provider, value = "local" },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedPendingMemoryRowsAsync(int count)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                """,
                new { hash = $"{Guid.NewGuid():N}-{i}", path = $"p{i}.md", value = $"pending row {i}" },
                cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    private async Task<long> EmbeddedMemoryCountAsync() => await CountAsync("entries", "embedded");

    private async Task<long> PendingMemoryCountAsync() => await CountAsync("entries", "pending");

    private async Task<long> CountAsync(string table, string embedState)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM {table} WHERE embed_state = @embedState",
            new { embedState }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Mirrors real activation: codeModel and codeEngine always land together (S1's engine guard needs a real value to compare against).</summary>
    private async Task ActivateCodeEngineAsync()
    {
        var modelDir = Path.Combine(_dataRoot, "models", "code-daemon-embed-v1");
        TestData.SeedCodeManifestDirectory(modelDir);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.CodeModel, value = modelDir },
            cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)",
            new { key = EmbeddingSettingsKeys.CodeEngine, value = $"test:local:{modelDir}" },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedPendingCodeRowsAsync(int count)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, @path, 1, 1, 'acme', 1, 1)
                """,
                new { hash = $"hash-{i}", path = $"src/File{i}.cs", value = $"class Sample{i} {{ }}" },
                cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    private async Task<long> EmbeddedCodeCountAsync() => await CountAsync("code_entries", "embedded");

    private async Task<long> PendingCodeCountAsync() => await CountAsync("code_entries", "pending");
}
