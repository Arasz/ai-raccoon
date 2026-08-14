using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

/// <summary>
///     Demonstrates the bootstrap idea (ADR-0039): where the deterministic HermesProcessNoisePolicy
///     fires, its hit is free labelled training data — appended to the real store via the real
///     bundled model. Wires real components end to end, but does NOT go through SqliteMemoryStore's
///     write path directly; nothing here decides what the accumulated samples mean (no scoring — see
///     docs/adr/0039). This proves the feedback plumbing, not a detector.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class NoiseLearningBootstrapTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-bootstrap");
    private readonly SqliteConnectionFactory _factory;
    private readonly HermesProcessNoisePolicy _hermesPolicy = new();

    public NoiseLearningBootstrapTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static string HermesLog(string procId, string command) =>
        $"[IMPORTANT: Background process {procId} completed normally (exit code 0).\nCommand: {command}\nOutput: ]";

    [Fact]
    public async Task HermesHit_FeedsTheCollector_AndASampleAppearsInTheRealStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryEmbedder = new EntryEmbedder(TestData.CreateEmbeddingService());
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await entryEmbedder.ConfigureAsync(connection, "local", BundledModel.ResolveModelPath(), null, ct);
        }

        var embedder = new SqliteContentEmbedder(_factory, entryEmbedder);
        var store = new SqliteNoiseClusterStore(_factory, NullLogger<SqliteNoiseClusterStore>.Instance);
        var collector = new NoiseFeedbackCollector(store, embedder);

        var request = new MemoryWriteRequest("proj-1", HermesLog("proc_74af2deb49a7", "dotnet test"), AgentId: "agent-1");
        var hermesResult = await _hermesPolicy.EvaluateAsync(request, ct);
        hermesResult.IsNoise.ShouldBeTrue("the fixture content must be a genuine Hermes hit for this test to demonstrate anything");

        await collector.ProcessFeedbackAsync(request.ProjectId, request.AgentId, request.Content, hermesResult.PolicyName!,
            cancellationToken: ct);

        var samples = await store.GetClustersAsync("proj-1", "agent-1", ct);
        samples.Count.ShouldBe(1);
        samples[0].Frequency.ShouldBe(1);
        samples[0].Status.ShouldBe("candidate");
        samples[0].CentroidEmbedding.Length.ShouldBe(384, "the real bundled model's embedding, not a fixed/fake vector");
    }

    [Fact]
    public async Task TwoHermesHits_AppendTwoSeparateSamples_NoMerging()
    {
        var ct = TestContext.Current.CancellationToken;
        var entryEmbedder = new EntryEmbedder(TestData.CreateEmbeddingService());
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await entryEmbedder.ConfigureAsync(connection, "local", BundledModel.ResolveModelPath(), null, ct);
        }

        var embedder = new SqliteContentEmbedder(_factory, entryEmbedder);
        var store = new SqliteNoiseClusterStore(_factory, NullLogger<SqliteNoiseClusterStore>.Instance);
        var collector = new NoiseFeedbackCollector(store, embedder);

        var first = new MemoryWriteRequest("proj-1", HermesLog("proc_aaa111", "dotnet build"), AgentId: "agent-1");
        var second = new MemoryWriteRequest("proj-1", HermesLog("proc_bbb222", "dotnet build"), AgentId: "agent-1");

        var firstHit = await _hermesPolicy.EvaluateAsync(first, ct);
        var secondHit = await _hermesPolicy.EvaluateAsync(second, ct);
        firstHit.IsNoise.ShouldBeTrue();
        secondHit.IsNoise.ShouldBeTrue();

        await collector.ProcessFeedbackAsync(first.ProjectId, first.AgentId, first.Content, firstHit.PolicyName!, cancellationToken: ct);
        await collector.ProcessFeedbackAsync(second.ProjectId, second.AgentId, second.Content, secondHit.PolicyName!, cancellationToken: ct);

        var samples = await store.GetClustersAsync("proj-1", "agent-1", ct);
        samples.Count.ShouldBe(2, "append-only (ADR-0039): even near-identical Hermes shapes land as two separate rows, no assignment/merging");
    }
}
