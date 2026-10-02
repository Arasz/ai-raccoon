using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Byte-exact stdout/stderr/exit/setting transcripts for the positive-integer setters
///     (performance, maintenance, extract) and the two report-then-apply repair commands, driven
///     from argv through <see cref="ConfigCommands" /> so option and argument names are exercised too.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliSetterTranscriptTests
{
    private static readonly string Nl = Environment.NewLine;

    public static TheoryData<string[], int, string, string, string?, string?> SetterCases => new()
    {
        { ["settings", "performance", "buffer-capacity", "5000"], 0, "buffer capacity: 5000 measurements (takes effect on the next server restart)\n", "", MetricsConfigKeys.BufferCapacityGlobal, "5000" },
        { ["settings", "performance", "buffer-capacity", "lots"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: buffer capacity must be a positive number of measurements\n", null, null },
        { ["settings", "performance", "buffer-capacity", "0"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: buffer capacity must be a positive number of measurements\n", null, null },
        { ["settings", "performance", "buffer-capacity", "-1"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: buffer capacity must be a positive number of measurements\n", null, null },
        { ["settings", "performance", "buffer-capacity", "1000001"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: buffer capacity must be at most 1000000 measurements\n", null, null },
        { ["settings", "performance", "flush-interval", "10"], 0, "flush interval: 10s (takes effect on the next flush tick)\n", "", MetricsConfigKeys.FlushIntervalSecondsGlobal, "10" },
        { ["settings", "performance", "flush-interval", "often"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: flush interval must be a positive number of seconds\n", null, null },
        { ["settings", "performance", "flush-interval", "0"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: flush interval must be a positive number of seconds\n", null, null },
        { ["settings", "performance", "retention", "14"], 0, "retention: 14 days (takes effect on the next maintenance pass)\n", "", MetricsConfigKeys.RetentionDaysGlobal, "14" },
        { ["settings", "performance", "retention", "forever"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: retention must be a positive number of days\n", null, null },
        { ["settings", "performance", "retention", "-7"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: retention must be a positive number of days\n", null, null },
        { ["settings", "performance", "retention", "36501"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: retention must be at most 36500 days\n", null, null },
        { ["settings", "maintenance", "interval", "60"], 0, "checkpoint interval: 60 min\n", "", BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal, "60" },
        { ["settings", "maintenance", "interval", "0"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: checkpoint interval must be a positive number of minutes\n", null, null },
        { ["settings", "maintenance", "interval", "often"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: checkpoint interval must be a positive number of minutes\n", null, null },
        { ["settings", "maintenance", "vacuum-interval", "7"], 0, "vacuum interval: 7 days\n", "", BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal, "7" },
        { ["settings", "maintenance", "vacuum-interval", "-1"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: vacuum interval must be a positive number of days\n", null, null },
        { ["settings", "maintenance", "vacuum-interval", "36501"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: vacuum interval must be at most 36500 days\n", null, null },
        { ["settings", "extract", "interval", "30"], 0, "extraction interval: 30 min\n", "", ExtractionConfigKeys.IntervalMinutesGlobal, "30" },
        { ["settings", "extract", "interval", "0"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: interval must be a positive number of minutes\n", null, null },
        { ["settings", "extract", "interval", "often"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: interval must be a positive number of minutes\n", null, null },
        { ["settings", "extract", "capacity", "3"], 0, "propose-tier capacity: 3 candidates\n", "", ExtractionConfigKeys.QueueCapacityGlobal, "3" },
        { ["settings", "extract", "capacity", "0"], ErrorCode.Usage.InvalidValue, "", "ai-raccoon: capacity must be a positive number of queued candidates\n", null, null },
        { ["settings", "maintenance", "embed-rows-per-run", "512"], 0, "embed rows per run: 512\n", "", BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal, "512" },
        { ["settings", "maintenance", "embed-rows-per-run", "0"], ErrorCode.Usage.InvalidValue, "", $"ai-raccoon: embed rows per run must be a positive integer, at most {BankMaintenanceConfigKeys.MaxEmbedRowsPerRun}\n", null, null }
    };

    [Theory]
    [MemberData(nameof(SetterCases))]
    public async Task Setter_PrintsExactTranscript_AndWritesOnlyOnSuccess(
        string[] argv, int exit, string stdout, string stderr, string? key, string? value)
    {
        var store = new FakeConfigStore();
        var commands = TestData.CreateConfigCommands(store,
            extract: new ExtractCommands(TestData.UnusedPromotionQueuePruneStore()),
            maintenance: new MaintenanceCommands(new InMemorySettings(), TestData.CreateInfrastructureOptions(Path.GetTempPath())),
            performance: new PerformanceCommands());

        var result = await CliRun.RunAsync(argv, commands);

        result.Exit.ShouldBe(exit);
        result.Out.ShouldBe(stdout.Replace("\n", Nl));
        result.Err.ShouldBe(stderr.Replace("\n", Nl));
        if (key is null)
        {
            store.Settings.ShouldBeEmpty();
        }
        else
        {
            store.Settings.ShouldContainKeyAndValue(key, value!);
            store.Settings.Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task PerformanceList_PrintsExactTranscript()
    {
        var store = new FakeConfigStore();
        store.Settings[MetricsConfigKeys.BufferCapacityGlobal] = "5000";
        store.Settings[MetricsConfigKeys.FlushIntervalSecondsGlobal] = "10";
        store.Settings[MetricsConfigKeys.RetentionDaysGlobal] = "14";

        var result = await CliRun.RunAsync(["settings", "performance", "list"],
            TestData.CreateConfigCommands(store, performance: new PerformanceCommands()));

        result.Exit.ShouldBe(0);
        result.Out.ShouldBe((
            "buffer capacity: 5000 measurements (takes effect on the next server restart)\n" +
            "flush interval: 10s (takes effect on the next flush tick)\n" +
            "retention: 14 days (takes effect on the next maintenance pass)\n").Replace("\n", Nl));
        result.Err.ShouldBe("");
    }

    [Theory]
    [InlineData(false, "chunk-index repair: 4 source group(s) examined, 2 row(s) would reposition (dry run; pass --apply to queue it), 1 row(s) set to the unknown position (-1), 3 row(s) given their partition's row count as total_chunks\n")]
    [InlineData(true, "chunk-index repair: 4 source group(s) examined, 2 row(s) queued for the server to reposition, 1 row(s) set to the unknown position (-1), 3 row(s) given their partition's row count as total_chunks\nchunk-index repair: request committed; the server applies it on its next maintenance poll (~15s).\n")]
    public async Task ChunkIndex_PrintsExactTranscript(bool apply, string stdout)
    {
        var repair = new InMemorySettings { ChunkIndexReport = new ChunkIndexRepairReport(4, 2, 1, 3) };

        var result = await RunRepairAsync(apply ? ["repair", "chunk-index", "--apply"] : ["repair", "chunk-index"], repair);

        result.Exit.ShouldBe(0);
        result.Out.ShouldBe(stdout.Replace("\n", Nl));
        result.Err.ShouldBe("");
        repair.LastRepairRequest.ShouldBe(apply ? RepairKind.ChunkIndex : null);
    }

    [Theory]
    [InlineData(false, 1, 3, 2, "reingest repair: 1 file(s) would reingest (dry run; pass --apply to queue it), 3 row(s) affected, 2 chunk(s) to embed\nreingest repair: per-row metadata (rating, access_count, last_accessed_at) would be discarded for the 3 affected row(s) — a re-chunk moves chunk boundaries, so hashes change and there is no 1:1 row to carry it onto.\n")]
    [InlineData(true, 1, 3, 2, "reingest repair: 1 file(s) queued for the server to reingest, 3 row(s) affected, 2 chunk(s) to embed\nreingest repair: per-row metadata (rating, access_count, last_accessed_at) will be discarded for the 3 affected row(s) — a re-chunk moves chunk boundaries, so hashes change and there is no 1:1 row to carry it onto.\nreingest repair: request committed; the server applies it and drains the resulting embeddings on its next maintenance poll (~15s) — nothing left to run by hand.\n")]
    [InlineData(false, 0, 0, 0, "reingest repair: 0 file(s) would reingest (dry run; pass --apply to queue it), 0 row(s) affected, 0 chunk(s) to embed\n")]
    [InlineData(true, 0, 0, 0, "reingest repair: 0 file(s) queued for the server to reingest, 0 row(s) affected, 0 chunk(s) to embed\nreingest repair: request committed; the server applies it and drains the resulting embeddings on its next maintenance poll (~15s) — nothing left to run by hand.\n")]
    public async Task Reingest_PrintsExactTranscript(bool apply, int files, int rows, int chunks, string stdout)
    {
        var repair = new InMemorySettings { ReingestReport = new ReingestRepairReport(files, rows, chunks) };

        var result = await RunRepairAsync(apply ? ["repair", "reingest", "--apply"] : ["repair", "reingest"], repair);

        result.Exit.ShouldBe(0);
        result.Out.ShouldBe(stdout.Replace("\n", Nl));
        result.Err.ShouldBe("");
        repair.LastRepairRequest.ShouldBe(apply ? RepairKind.Reingest : null);
    }

    private static Task<(int Exit, string Out, string Err)> RunRepairAsync(string[] argv, InMemorySettings repair) =>
        CliRun.RunAsync(argv, TestData.CreateConfigCommands(new FakeConfigStore(),
            chunkIndexRepair: new ChunkIndexRepairCommands(repair),
            reingestRepair: new ReingestRepairCommands(repair)));
}
