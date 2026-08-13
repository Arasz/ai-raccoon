using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>memory_share_extract must score candidates against every known project — the same
/// scope the background loop uses — not just the caller's requested subset.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ShareExtractScoringScopeTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static ExtractionCandidateRow Row(string hash, string value) => new(hash, $"{hash}.md", value, null, 0.5, 0, FixedNow.AddDays(-5), null);

    [Fact]
    public async Task ShareExtract_ScoresTheSameCandidate_AsTheBackgroundLoop()
    {
        // "acme" and "beta" both exist in the bank; the candidate lives under "acme" and mentions
        // "beta", which is NOT among the ids requested below — where the +2 cross-project bonus
        // is wrongly withheld today.
        var store = new FakeExtractionStore();
        var queue = new FakePromotionQueue();
        var time = new FakeTimeProvider(FixedNow);
        var runner = new SharedExtractionRunner(store, new SharedExtractionService(), queue, time);
        store.Candidates["acme"] = [Row("h1", "organic fact about beta")];
        store.Settings[ExtractionConfigKeys.EnabledGlobal] = "true";

        var gate = new ToolGate(new MemoryAccessGuard(store), queue);
        var shareTools = new ShareTools(store, gate, runner, queue);
        var toolResult = await shareTools.ShareExtract(["acme"],
            cancellationToken: TestContext.Current.CancellationToken);
        var scoreViaTool = toolResult.Data!.Candidates.Single(c => c.Hash == "h1").Score;

        var hostedService = new ExtractionHostedService(store, runner, queue, time, TestTelemetry.None,
            NullLogger<ExtractionHostedService>.Instance);
        await hostedService.RunOnceAsync(TestContext.Current.CancellationToken);
        var scoreViaLoop = queue.LastCandidates!.Single(c => c.Hash == "h1").Score;

        scoreViaTool.ShouldBe(scoreViaLoop,
            $"memory_share_extract scored h1 at {scoreViaTool}, the background loop scored it at " +
            $"{scoreViaLoop} — same candidate, same bank, different scoring scope");
    }

    [Fact]
    public async Task ShareExtract_WideningTheScoringScope_DoesNotWidenWhatGetsProposed()
    {
        // "beta" also has a candidate; the tool call below only requests "acme". Scoring "acme"'s
        // candidate against the full project list must not pull "beta"'s own candidates in.
        var store = new FakeExtractionStore();
        var queue = new FakePromotionQueue();
        var time = new FakeTimeProvider(FixedNow);
        var runner = new SharedExtractionRunner(store, new SharedExtractionService(), queue, time);
        store.Candidates["acme"] = [Row("h1", "organic fact about beta")];
        store.Candidates["beta"] = [Row("h2", "organic fact about acme")];

        var gate = new ToolGate(new MemoryAccessGuard(store), queue);
        var shareTools = new ShareTools(store, gate, runner, queue);
        var toolResult = await shareTools.ShareExtract(["acme"],
            cancellationToken: TestContext.Current.CancellationToken);

        toolResult.Data!.Candidates.Select(c => c.Hash).ShouldBe(["h1"]);
    }
}
