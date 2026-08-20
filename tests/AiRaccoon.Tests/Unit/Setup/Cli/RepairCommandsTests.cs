using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
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
