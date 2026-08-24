using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Issue #466's second defect: a code row that cannot embed used to reach
///     <see cref="CodeCorpusSchema.MaxEmbedAttempts" /> and drop out of the drain's selection
///     forever without a single log line — the only evidence was a column value or a much later
///     search error. Every failed attempt must carry its exception, and crossing the ceiling must
///     name the row that was given up on.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeEmbedderPoisonLoggingTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-embedder-logging");
    private SqliteConnectionFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task EmbedPendingBatchAsync_RowCannotEmbed_LogsAWarningCarryingTheException()
    {
        var logger = new FakeLogger<CodeEmbedder>();
        var embedder = new CodeEmbedder(PoisonService(), logger, new VecDimensionReconciler());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection);
        await SeedPoisonCodeRowAsync(connection, id: 1);

        await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        var record = logger.Collector.GetSnapshot().Where(r => r.Level >= LogLevel.Warning)
            .ShouldHaveSingleItem("one failed row, one warning — the batch call and its per-row retry are one attempt");
        record.Level.ShouldBe(LogLevel.Warning);
        record.Exception.ShouldNotBeNull("a failed embed attempt without its exception is unactionable")
            .Message.ShouldBe("simulated poison row: this content can never embed");
        record.Message.ShouldContain("src/Poison1.cs", Case.Sensitive);
    }

    [Fact]
    public async Task EmbedPendingBatchAsync_RowCrossesTheAttemptCeiling_LogsAnErrorNamingTheRow()
    {
        var logger = new FakeLogger<CodeEmbedder>();
        var embedder = new CodeEmbedder(PoisonService(), logger, new VecDimensionReconciler());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection);
        await SeedPoisonCodeRowAsync(connection, id: 1);

        for (var attempt = 0; attempt < CodeCorpusSchema.MaxEmbedAttempts; attempt++)
        {
            await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);
        }

        var records = logger.Collector.GetSnapshot();
        records.Count(r => r.Level == LogLevel.Warning)
            .ShouldBe(CodeCorpusSchema.MaxEmbedAttempts, "one warning per attempt");
        var giveUp = records.Where(r => r.Level == LogLevel.Error).ShouldHaveSingleItem(
            "crossing the ceiling is the moment the row is abandoned, and it happens exactly once");
        giveUp.Message.ShouldContain("src/Poison1.cs", Case.Sensitive);
        giveUp.Message.ShouldContain("src/Poison1.py", Case.Sensitive);
        giveUp.Message.ShouldContain(CodeCorpusSchema.MaxEmbedAttempts.ToString());
    }

    [Fact]
    public async Task EmbedPendingBatchAsync_EveryRowEmbeds_LogsNothingAtWarningOrAbove()
    {
        var logger = new FakeLogger<CodeEmbedder>();
        var embedder = new CodeEmbedder(new FakeCodeEmbeddingService(), logger, new VecDimensionReconciler());
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await ActivateCodeModelAsync(connection);
        await SeedHealthyCodeRowAsync(connection, id: 1);

        var processed = await embedder.EmbedPendingBatchAsync(connection, 32, TestContext.Current.CancellationToken);

        processed.ShouldBe(1);
        logger.Collector.GetSnapshot().ShouldNotContain(r => r.Level >= LogLevel.Warning,
            "a healthy drain must stay silent, or the signal is worth nothing");
    }

    private static FakeCodeEmbeddingService PoisonService()
    {
        var fake = new FakeCodeEmbeddingService();
        fake.PoisonValues.Add("class Poison { }");
        return fake;
    }

    private static async Task ActivateCodeModelAsync(SqliteConnection connection)
    {
        const string directory = "/models/code-daemon-embed-v1";
        await UpsertSettingAsync(connection, EmbeddingSettingsKeys.CodeModel, directory);
        await UpsertSettingAsync(connection, EmbeddingSettingsKeys.CodeEngine, $"test:local:{directory}");
    }

    private static async Task UpsertSettingAsync(SqliteConnection connection, string key, string value) =>
        await connection.ExecuteAsync(
            "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value });

    private static async Task SeedPoisonCodeRowAsync(SqliteConnection connection, long id) =>
        await SeedAsync(connection, id, $"src/Poison{id}.cs", $"src/Poison{id}.py", "class Poison { }");

    private static async Task SeedHealthyCodeRowAsync(SqliteConnection connection, long id) =>
        await SeedAsync(connection, id, $"src/File{id}.cs", $"src/File{id}.py", $"class Sample{id} {{ }}");

    private static async Task SeedAsync(SqliteConnection connection, long id, string path, string sourceFile, string value) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @sourceFile, 1, 1, 'acme', 1, 1)
            """,
            new { id, hash = $"hash-{id}", path, sourceFile, value });
}
