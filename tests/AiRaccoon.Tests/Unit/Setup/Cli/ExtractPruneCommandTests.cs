using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     `extract prune` is a thin client over <see cref="IPromotionQueuePruneStore" /> (ADR-0075
///     amendment): the report comes back already computed, and the CLI process never opens the bank
///     — <see cref="FakePromotionQueuePruneStore" /> stands in for the acquired server connection,
///     so these run as fast unit tests rather than against a real bank.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ExtractPruneCommandTests
{
    [Fact]
    public async Task DryRun_NeverRequestsAPrune()
    {
        var store = new FakePromotionQueuePruneStore { Report = OrphanReport(2) };

        await RunAsync(apply: false, store);

        store.RequestCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task Apply_RequestsThePrune()
    {
        var store = new FakePromotionQueuePruneStore { Report = OrphanReport(2) };

        await RunAsync(apply: true, store);

        store.RequestCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task DryRun_SaysFoundNotQueued()
    {
        var stdout = await RunAsync(apply: false, new FakePromotionQueuePruneStore { Report = OrphanReport(1) });

        stdout.ShouldContain("found (dry run; pass --apply to remove)");
        stdout.ShouldNotContain("queued for the server");
    }

    /// <summary>
    ///     The behavioural fix this task exists for: --apply must not claim the removal already
    ///     happened, and must not claim a manual fallback exists — the server applies it on its next
    ///     maintenance poll, same wording family as `repair`'s.
    /// </summary>
    [Fact]
    public async Task Apply_StatesTheServerAppliesItOnItsNextPoll_NotAnImmediateRemoval()
    {
        var stdout = await RunAsync(apply: true, new FakePromotionQueuePruneStore { Report = OrphanReport(1) });

        stdout.ShouldContain("queued for the server to remove");
        stdout.ShouldContain("request committed");
        stdout.ShouldContain("maintenance poll");
    }

    [Fact]
    public async Task NoOrphans_NeverRequestsAPrune_EvenWithApply()
    {
        var store = new FakePromotionQueuePruneStore
        {
            Report = new PromotionQueueOrphanReport(0, new Dictionary<string, int>(StringComparer.Ordinal))
        };

        var stdout = await RunAsync(apply: true, store);

        store.RequestCalled.ShouldBeFalse();
        stdout.ShouldContain("no orphaned candidates found");
    }

    private static PromotionQueueOrphanReport OrphanReport(int count) =>
        new(count, new Dictionary<string, int>(StringComparer.Ordinal) { ["acme"] = count });

    private static async Task<string> RunAsync(bool apply, FakePromotionQueuePruneStore store)
    {
        var argv = apply ? new[] { "extract", "prune", "--apply" } : ["extract", "prune"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ExtractCommands(store).PruneAsync(parsed!.ParsedCliArgs,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }

    private sealed class FakePromotionQueuePruneStore : IPromotionQueuePruneStore
    {
        public PromotionQueueOrphanReport Report { get; set; } =
            new(0, new Dictionary<string, int>(StringComparer.Ordinal));

        public bool RequestCalled { get; private set; }

        public Task<PromotionQueueOrphanReport> ReportPruneOrphansAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Report);

        public Task RequestPruneOrphansAsync(CancellationToken cancellationToken = default)
        {
            RequestCalled = true;
            return Task.CompletedTask;
        }
    }
}
