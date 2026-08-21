using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     What a real bank experiences the first time a 1.17.0 process opens it: the v9 migration, then
///     the maintenance jobs that are due. Every number this project has published about that sequence
///     was measured piecemeal — this runs the whole thing once, on a copy, so the claim is one
///     measurement rather than three predictions stitched together.
///     <para>
///         Points at whatever bank <see cref="BankEnvVar" /> names and asserts only what must be true
///         of any bank afterwards. It rewrites the file it is given: never point it at a live bank.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class FirstContactRehearsal(ITestOutputHelper output)
{
    private const string BankEnvVar = "AIRACCOON_FIRST_CONTACT_BANK";

    /// <summary>How many pending rows to embed as a throughput sample. Unset skips it.</summary>
    private const string EmbedSampleEnvVar = "AIRACCOON_FIRST_CONTACT_EMBED_SAMPLE";

    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly TimeProvider _timeProvider = new FakeTimeProvider();

    [Fact]
    public async Task Rehearse_TheFirstOpenByANewBinary()
    {
        var bank = Environment.GetEnvironmentVariable(BankEnvVar);
        if (bank is null)
        {
            output.WriteLine($"{BankEnvVar} not set — rehearses a 1.17.0 first open against a bank COPY.");
            return;
        }

        File.Exists(bank).ShouldBeTrue($"{BankEnvVar} must name an existing bank file");

        await using var connection = new SqliteConnection($"Data Source={bank}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();

        var fileBefore = new FileInfo(bank).Length;
        var rowsBefore = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries");
        var charsBefore = await connection.ExecuteScalarAsync<long>("SELECT SUM(length(value)) FROM entries");
        var overBefore = await OverWindowAsync(connection);

        var migrateStarted = DateTimeOffset.UtcNow;
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var migrateElapsed = DateTimeOffset.UtcNow - migrateStarted;

        var jobsStarted = DateTimeOffset.UtcNow;
        var outcomes = await new MaintenanceJobRunner(TimeProvider.System,
                NullLogger<MaintenanceJobRunner>.Instance)
            .RunDueAsync(connection,
            [
                new ChunkBackfillJob(TestData.RealMarkdownChunker(), TimeProvider.System, TestData.CreateEmbeddingService()),
                new Vec0ReclaimJob(),
                new VacuumJob()
            ], TestContext.Current.CancellationToken);
        var jobsElapsed = DateTimeOffset.UtcNow - jobsStarted;

        var rowsAfter = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries");
        var charsAfter = await connection.ExecuteScalarAsync<long>("SELECT SUM(length(value)) FROM entries");
        var pending = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM entries WHERE embed_state = 'pending'");

        output.WriteLine($"migration        : {migrateElapsed.TotalSeconds:F1}s");
        foreach (var outcome in outcomes)
        {
            output.WriteLine($"job {outcome.Name,-15}: {(outcome.Ran ? "ran" : "skipped")}{(outcome.Error is null ? "" : " — " + outcome.Error)}");
        }

        output.WriteLine($"jobs total       : {jobsElapsed.TotalSeconds:F1}s");
        output.WriteLine($"rows             : {rowsBefore:N0} -> {rowsAfter:N0}");
        output.WriteLine($"chars            : {charsBefore:N0} -> {charsAfter:N0} ({charsAfter - charsBefore:+#;-#;0})");
        output.WriteLine($"over-window rows : {overBefore} -> {await OverWindowAsync(connection)}");
        output.WriteLine($"file             : {fileBefore:N0} -> {new FileInfo(bank).Length:N0} ({new FileInfo(bank).Length - fileBefore:+#;-#;0})");
        output.WriteLine($"pending embeds   : {pending:N0}");

        // The one number nobody had: how long the pending backlog takes to drain. The 2026-08-09
        // review flagged "no embedding throughput" as a gap and it was still open, so the drain time
        // was being asserted by nobody and guessed by everyone. Sampled, not extrapolated blind.
        if (pending > 0 && Environment.GetEnvironmentVariable(EmbedSampleEnvVar) is { } rawSample
                        && int.TryParse(rawSample, out var sample) && sample > 0)
        {
            var project = await connection.ExecuteScalarAsync<string?>(
                "SELECT project_id FROM entries WHERE embed_state = 'pending' AND project_id IS NOT NULL LIMIT 1");
            var embedder = new EntryEmbedder(TestData.CreateEmbeddingService(), _modelMigrationLease, _timeProvider);
            var started = DateTimeOffset.UtcNow;
            var embedded = await embedder.EmbedPendingAsync(connection, project ?? "acme", sample,
                TestContext.Current.CancellationToken);
            var elapsed = DateTimeOffset.UtcNow - started;

            var perRow = elapsed.TotalMilliseconds / Math.Max(1, embedded);
            output.WriteLine($"embed sample     : {embedded} rows in {elapsed.TotalSeconds:F1}s "
                             + $"({perRow:F0} ms/row)");
            output.WriteLine($"drain estimate   : {TimeSpan.FromMilliseconds(perRow * pending).TotalMinutes:F1} min "
                             + $"for {pending:N0} rows — EXTRAPOLATED from {embedded}, not measured whole");
        }

        outcomes.ShouldAllBe(o => o.Error == null, "a job that errors on a real bank is the finding");
    }

    private static async Task<int> OverWindowAsync(SqliteConnection connection)
    {
        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        var budget = EmbeddingService.SafeChunkBudgetFor("local", null);
        var values = await connection.QueryAsync<string>("SELECT value FROM entries WHERE value IS NOT NULL");
        return values.Count(v => tokenizer.CountTokens(v) > budget);
    }
}
