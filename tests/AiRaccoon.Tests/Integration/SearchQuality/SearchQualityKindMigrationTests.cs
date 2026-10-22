using AiRaccoon.Core.Memory;
using System.Collections.Generic;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.SearchQuality;

/// <summary>
///     v21→v22: the search-quality rows learn the entry types a query asked for. The old
///     <c>kind</c> column records the corpus kind (memory | code | both); the new
///     <c>kind_mask</c> records the query parameter's flag set. The old <c>memory</c> covered agent
///     notes AND ingested chunks, which the new vocabulary splits — so an old <c>memory</c> row
///     means the SUM of the two, and mapping it to Memory alone would change what the row recorded.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchQualityKindMigrationTests
{
    /// <summary>One historical row: its old kind, and the flag set that kind stands for.</summary>
    private sealed record HistoricalRow(string CorrelationId, string Kind, int ExpectedMask, int Grade);

    private sealed record QualityKindRow(string? Kind, long? Mask);

    private static readonly HistoricalRow[] Rows =
    [
        // The old 'memory' meant agent notes AND ingested chunks: Memory AND File.
        new("c-memory", "memory", (int)(EntryType.Memory | EntryType.File), 4),
        // The old 'code' meant the code corpus alone.
        new("c-code", "code", (int)EntryType.Code, 5),
        // The old 'both' meant everything.
        new("c-both", "both", (int)(EntryType.Memory | EntryType.File | EntryType.Code), 3)
    ];

    [RetryFact]
    public async Task EnsureAsync_UpgradesTheHistoricalRows_KeepingTheirMeaning()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var originalIds = new Dictionary<string, long>();
        foreach (var row in Rows)
        {
            await SeedAsync(connection, row);
            originalIds[row.CorrelationId] = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM search_quality WHERE correlation_id = @id", new { id = row.CorrelationId });
        }

        // A row older than the kind column: it ran the memory leg and said nothing about which.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO search_quality (correlation_id, query, created_at) VALUES ('c-none', 'q', 1)",
            cancellationToken: TestContext.Current.CancellationToken));

        // The bank goes back to before the column.
        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE search_quality DROP COLUMN kind_mask", cancellationToken: TestContext.Current.CancellationToken));
        await StampAsync(connection, 21);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        foreach (var row in Rows)
        {
            var mask = await ReadMaskAsync(connection, row.CorrelationId);
            mask.ShouldBe(row.ExpectedMask,
                $"the historical row '{row.CorrelationId}' must keep its meaning: the old '{row.Kind}' covered more than one kind");

            var grade = await connection.ExecuteScalarAsync<int?>(
                "SELECT usefulness_grade FROM search_quality WHERE correlation_id = @id",
                new { id = row.CorrelationId });
            grade.ShouldBe(row.Grade, "the migration must not touch the grades");

            var id = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM search_quality WHERE correlation_id = @id",
                new { id = row.CorrelationId });
            id.ShouldBe(originalIds[row.CorrelationId], "the migration must not renumber the rows");
            (await connection.ExecuteScalarAsync<string>(
                "SELECT follow_through_files FROM search_quality WHERE correlation_id = @id",
                new { id = row.CorrelationId })).ShouldBe("[\"/used.md\"]");
        }

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadMaskAsync(connection, "c-none")).ShouldBeNull(
            "a row that said nothing about which kinds it asked for says nothing now either");
    }

    /// <summary>
    ///     The old API still records what it always did: the corpus kind goes in beside the mask,
    ///     and a consumer of the old column reads it unchanged.
    /// </summary>
    [RetryFact]
    public async Task TheOldApi_StillRecordsTheOldKind()
    {
        var dataRoot = TestData.CreateTempRoot("air-quality-kind-migration");
        try
        {
            var options = new AiRaccoon.Infrastructure.Options.InfrastructureOptions
            {
                DataRoot = dataRoot,
                Rid = "osx-arm64",
                Scope = AiRaccoon.Infrastructure.Options.InstallScope.User
            };
            var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            var service = new SqliteSearchQualityService(factory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSearchQualityService>.Instance);

            await service.RecordSearchAsync("corr-1", "q", "project", "acme", "memory", "sess", 3, ["/a.md"],
                TestContext.Current.CancellationToken);

            await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            var row = (await connection.QueryAsync<QualityKindRow>(
                "SELECT kind, kind_mask AS Mask FROM search_quality WHERE correlation_id = 'corr-1'")).Single();
            row.Kind.ShouldBe("memory", "the old column still records the old vocabulary");
            row.Mask.ShouldBe((int)(EntryType.Memory | EntryType.File),
                "and the mask beside it says what that old kind meant in the new vocabulary");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>The mapping itself, stated once: the old vocabulary's meaning in the new one.</summary>
    [Fact]
    public void TheOldVocabulary_MeansWhatItMeant()
    {
        SearchQualityKinds.MaskOf("memory").ShouldBe(EntryType.Memory | EntryType.File);
        SearchQualityKinds.MaskOf("code").ShouldBe(EntryType.Code);
        SearchQualityKinds.MaskOf("both").ShouldBe(EntryType.Memory | EntryType.File | EntryType.Code);
        SearchQualityKinds.MaskOf(null).ShouldBeNull();
    }

    private static async Task SeedAsync(SqliteConnection connection, HistoricalRow row)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO search_quality (correlation_id, query, scope, project_id, session_id, kind,
                result_count, usefulness_grade, follow_through_files, created_at)
            VALUES (@id, 'q', 'project', 'acme', 'sess', @kind, 1, @grade, '["/used.md"]', 1)
            """,
            new { id = row.CorrelationId, kind = row.Kind, grade = row.Grade },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<int?> ReadMaskAsync(SqliteConnection connection, string correlationId) =>
        await connection.ExecuteScalarAsync<int?>(
            "SELECT kind_mask FROM search_quality WHERE correlation_id = @id",
            new { id = correlationId });

    private static async Task StampAsync(SqliteConnection connection, int version)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {version}", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA application_id = 0", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
