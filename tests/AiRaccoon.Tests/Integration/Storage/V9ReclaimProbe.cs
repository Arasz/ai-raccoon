using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Does ladder step v9 actually give a user back any disk? Rebuilding the vec0 tables frees pages
///     into SQLite's free list; the file itself does not shrink until VACUUM. This measures both
///     halves on a copy of a real bank: pages freed by the migration, and bytes returned by a VACUUM
///     after it — plus how long that VACUUM costs, because it is the number any "just vacuum after
///     migrating" proposal has to justify.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class V9ReclaimProbe
{
    internal const string BankEnvVar = "AIRACCOON_V9_RECLAIM_BANK";

    private readonly ITestOutputHelper _output;

    public V9ReclaimProbe(ITestOutputHelper output) => _output = output;

    [RetryFact]
    public async Task Probe_MeasuresWhatV9FreesAndWhatVacuumReturns()
    {
        var bank = Environment.GetEnvironmentVariable(BankEnvVar);
        if (bank is null)
        {
            _output.WriteLine($"{BankEnvVar} not set — measures v9's page reclaim and the VACUUM that realises it.");
            return;
        }

        File.Exists(bank).ShouldBeTrue($"{BankEnvVar} must name an existing bank file");

        await using var connection = new SqliteConnection($"Data Source={bank}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();

        var fileBefore = new FileInfo(bank).Length;
        var versionBefore = await connection.ExecuteScalarAsync<long>("PRAGMA user_version");
        var freeBefore = await connection.ExecuteScalarAsync<long>("PRAGMA freelist_count");

        var migrateStarted = DateTimeOffset.UtcNow;
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var migrateElapsed = DateTimeOffset.UtcNow - migrateStarted;

        var pageSize = await connection.ExecuteScalarAsync<long>("PRAGMA page_size");
        var freeAfter = await connection.ExecuteScalarAsync<long>("PRAGMA freelist_count");
        var fileAfterMigrate = new FileInfo(bank).Length;

        var vacuumStarted = DateTimeOffset.UtcNow;
        await connection.ExecuteAsync("VACUUM");
        var vacuumElapsed = DateTimeOffset.UtcNow - vacuumStarted;
        var fileAfterVacuum = new FileInfo(bank).Length;

        _output.WriteLine($"user_version      : {versionBefore} -> {await connection.ExecuteScalarAsync<long>("PRAGMA user_version")}");
        _output.WriteLine($"page_size         : {pageSize}");
        _output.WriteLine($"freelist pages    : {freeBefore} -> {freeAfter} (+{freeAfter - freeBefore})");
        _output.WriteLine($"freed by migration: {(freeAfter - freeBefore) * pageSize:N0} bytes, held in the free list");
        _output.WriteLine($"migration elapsed : {migrateElapsed.TotalSeconds:F1}s");
        _output.WriteLine($"file before       : {fileBefore:N0}");
        _output.WriteLine($"file after migrate: {fileAfterMigrate:N0} ({fileAfterMigrate - fileBefore:+#;-#;0})");
        _output.WriteLine($"file after VACUUM : {fileAfterVacuum:N0} ({fileAfterVacuum - fileBefore:+#;-#;0})");
        _output.WriteLine($"VACUUM elapsed    : {vacuumElapsed.TotalSeconds:F1}s");
    }
}
