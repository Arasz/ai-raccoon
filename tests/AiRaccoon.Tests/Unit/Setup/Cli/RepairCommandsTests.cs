using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     `repair reingest`/`repair chunk-index` are thin clients over <see cref="IRepairStore" />
///     (ADR-0075 amendment): the report comes back already computed, and the CLI process never opens
///     the bank — <see cref="InMemorySettings" /> stands in for the acquired server connection, so
///     these run as fast unit tests rather than against a real bank.
///     <para>
///         `repair reingest` discards per-row metadata as a consequence of the re-chunk, not a
///         hazard to work around (owner ruling) — so an operator must see that stated before AND
///         after --apply, never only in one of the two.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RepairCommandsTests
{
    [Fact]
    public async Task Reingest_DryRun_WarnsAboutMetadataLoss()
    {
        var stdout = await RunReingestAsync(apply: false, new ReingestRepairReport(1, 3, 2));

        stdout.ShouldContain("metadata");
        stdout.ShouldContain("rating");
        stdout.ShouldContain("discarded");
    }

    [Fact]
    public async Task Reingest_Apply_WarnsAboutMetadataLoss()
    {
        var stdout = await RunReingestAsync(apply: true, new ReingestRepairReport(1, 3, 2));

        stdout.ShouldContain("metadata");
        stdout.ShouldContain("rating");
        stdout.ShouldContain("discarded");
    }

    /// <summary>
    ///     The three-string fix this task exists for: the old text claimed "with no server running,
    ///     nothing drains it — run memory_embed_pending by hand", which enshrined the CLI-writes-the-
    ///     bank defect as expected behaviour. The server now applies AND drains — there is no manual
    ///     path in the normal case, and the CLI must not advertise one.
    /// </summary>
    [Fact]
    public async Task Reingest_Apply_StatesTheServerAppliesAndDrains_NotAManualFallback()
    {
        var stdout = await RunReingestAsync(apply: true, new ReingestRepairReport(1, 3, 2));

        stdout.ShouldContain("server applies");
        stdout.ShouldContain("maintenance poll");
        stdout.ShouldNotContain("memory_embed_pending");
        stdout.ShouldNotContain("no server running");
    }

    [Fact]
    public async Task Reingest_DryRun_NeverRequestsARepair()
    {
        var inner = new InMemorySettings { ReingestReport = new ReingestRepairReport(1, 3, 2) };

        await RunReingestAsync(apply: false, inner);

        inner.LastRepairRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Reingest_Apply_RequestsTheReingestKind()
    {
        var inner = new InMemorySettings { ReingestReport = new ReingestRepairReport(1, 3, 2) };

        await RunReingestAsync(apply: true, inner);

        inner.LastRepairRequest.ShouldBe(RepairKind.Reingest);
    }

    [Fact]
    public async Task ChunkIndex_Apply_QueuesForTheServer_AndDoesNotClaimToWriteLocally()
    {
        var stdout = await RunChunkIndexAsync(apply: true, new ChunkIndexRepairReport(4, 2, 1));

        stdout.ShouldContain("queued for the server");
        stdout.ShouldContain("maintenance poll");
    }

    [Fact]
    public async Task ChunkIndex_Apply_RequestsTheChunkIndexKind()
    {
        var inner = new InMemorySettings { ChunkIndexReport = new ChunkIndexRepairReport(4, 2, 1) };

        await RunChunkIndexAsync(apply: true, inner);

        inner.LastRepairRequest.ShouldBe(RepairKind.ChunkIndex);
    }

    /// <summary>
    ///     Formatter row (review MUST-4: dispatcher/formatter only, never the diagnose gate — that is
    ///     Diagnose_ListsJsaaCluster alone): dry-run output names the loser, the winner, and the fold.
    ///     Ledger — drop-folds-to-line : --filter ProjectIds_DryRun_FormatsTheFoldPlan : jsaa-cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_FormatsTheFoldPlan()
    {
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, ClusterReport());

        stdout.ShouldContain("job-search-ai-assistant");
        stdout.ShouldContain("jsaa");
        stdout.ShouldContain("folds to");
    }

    /// <summary>
    ///     Dispatcher row (review MUST-4, never the diagnose gate): --diagnose reports without committing a request.
    ///     Ledger — diagnose-requests-anyway : --filter ProjectIds_DiagnoseFlag_ReportsWithoutRequesting : cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DiagnoseFlag_ReportsWithoutRequesting()
    {
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsDiagnoseAsync(inner);

        stdout.ShouldContain("job-search-ai-assistant");
        inner.LastRepairRequest.ShouldBeNull();
    }

    /// <summary>
    ///     Dispatcher row (review MUST-4, never the diagnose gate): dry run never commits.
    ///     Ledger — dry-run-requests : --filter ProjectIds_DryRun_NeverRequestsARepair : cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_NeverRequestsARepair()
    {
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        await RunProjectIdsAsync(apply: false, inner);

        inner.LastRepairRequest.ShouldBeNull();
    }

    /// <summary>
    ///     Dispatcher row (review MUST-4, never the diagnose gate): --apply commits the project-ids kind.
    ///     Ledger — apply-requests-wrong-kind : --filter ProjectIds_Apply_RequestsTheProjectIdsKind : cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Apply_RequestsTheProjectIdsKind()
    {
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsAsync(apply: true, inner);

        inner.LastRepairRequest.ShouldBe(RepairKind.ProjectIds);
        stdout.ShouldContain("maintenance poll");
    }

    private static ProjectIdCensusReport ClusterReport() => new(
        [
            new ProjectIdCensusRow("jsaa", false, null, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            new ProjectIdCensusRow("job-search-ai-assistant", false, null, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])
        ], 0, 0, 0, 0, []);

    private static Task<string> RunProjectIdsAsync(bool apply, bool diagnose, ProjectIdCensusReport report) =>
        RunProjectIdsAsync(apply, diagnose, new InMemorySettings { ProjectIdsReport = report });

    private static Task<string> RunProjectIdsAsync(bool apply, InMemorySettings store) =>
        RunProjectIdsAsync(apply, false, store);

    private static async Task<string> RunProjectIdsDiagnoseAsync(InMemorySettings store) =>
        await RunProjectIdsAsync(false, true, store);

    private static async Task<string> RunProjectIdsAsync(bool apply, bool diagnose, InMemorySettings store)
    {
        var argv = apply
            ? new[] { "repair", "project-ids", "--apply" }
            : diagnose
                ? ["repair", "project-ids", "--diagnose"]
                : ["repair", "project-ids"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ProjectIdsRepairCommands(store).RunAsync(parsed!.ParsedCliArgs,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }

    [Fact]
    public async Task ChunkIndex_DryRun_NeverRequestsARepair()
    {
        var inner = new InMemorySettings { ChunkIndexReport = new ChunkIndexRepairReport(4, 2, 1) };

        await RunChunkIndexAsync(apply: false, inner);

        inner.LastRepairRequest.ShouldBeNull();
    }

    private static Task<string> RunReingestAsync(bool apply, ReingestRepairReport report) => RunReingestAsync(apply, new InMemorySettings { ReingestReport = report });

    private static async Task<string> RunReingestAsync(bool apply, InMemorySettings store)
    {
        var argv = apply ? new[] { "repair", "reingest", "--apply" } : ["repair", "reingest"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ReingestRepairCommands(store).RunAsync(parsed!.ParsedCliArgs,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }

    private static Task<string> RunChunkIndexAsync(bool apply, ChunkIndexRepairReport report) => RunChunkIndexAsync(apply, new InMemorySettings { ChunkIndexReport = report });

    private static async Task<string> RunChunkIndexAsync(bool apply, InMemorySettings store)
    {
        var argv = apply ? new[] { "repair", "chunk-index", "--apply" } : ["repair", "chunk-index"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ChunkIndexRepairCommands(store).RunAsync(parsed!.ParsedCliArgs,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }
}
