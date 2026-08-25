using AiRaccoon.Infrastructure.Ingestion;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Runs <see cref="ChunkBackfill" /> against whatever bank <see cref="BankEnvVar" /> names, so the
///     pass can be rehearsed on a COPY before it is ever pointed at a live bank. Asserts nothing about
///     the product; it prints what the pass did.
///     <para>
///         Set <see cref="ApplyEnvVar" /> to write. Without it the run is a dry run — the safe default
///         matters here, because the difference between the two is 6,240 rewritten rows.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkBackfillTrialRun
{
    internal const string BankEnvVar = "AIRACCOON_BACKFILL_TRIAL_BANK";
    internal const string ApplyEnvVar = "AIRACCOON_BACKFILL_TRIAL_APPLY";

    private readonly ITestOutputHelper _output;

    public ChunkBackfillTrialRun(ITestOutputHelper output) => _output = output;

    [RetryFact]
    public async Task TrialRun_ReportsWhatTheBackfillWouldDo()
    {
        var bank = Environment.GetEnvironmentVariable(BankEnvVar);
        if (bank is null)
        {
            _output.WriteLine($"{BankEnvVar} not set — rehearses WP3's chunk backfill against a bank copy.");
            return;
        }

        File.Exists(bank).ShouldBeTrue($"{BankEnvVar} must name an existing bank file");
        var apply = Environment.GetEnvironmentVariable(ApplyEnvVar) is not null;

        await using var connection = new SqliteConnection($"Data Source={bank}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();

        var backfill = new ChunkBackfill(TestData.RealMarkdownChunker(), TimeProvider.System, TestData.CreateEmbeddingService());
        var started = DateTimeOffset.UtcNow;
        var report = await backfill.RunAsync(connection, dryRun: !apply, TestContext.Current.CancellationToken);
        var elapsed = DateTimeOffset.UtcNow - started;

        _output.WriteLine($"bank           : {bank}");
        _output.WriteLine($"mode           : {(apply ? "APPLY (writing)" : "dry run")}");
        _output.WriteLine($"rows examined  : {report.RowsExamined}");
        _output.WriteLine($"rows replaced  : {report.RowsReplaced}");
        _output.WriteLine($"pieces written : {report.PiecesWritten}");
        _output.WriteLine($"chars before   : {report.CharsBefore}");
        _output.WriteLine($"chars after    : {report.CharsAfter} ({report.CharsAfter - report.CharsBefore:+#;-#;0})");
        _output.WriteLine($"elapsed        : {elapsed.TotalSeconds:F1}s");

        if (apply)
        {
            var pending = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM entries WHERE embed_state = 'pending'");
            _output.WriteLine($"pending rows   : {pending} (the embed pass owns these)");
        }
    }
}
