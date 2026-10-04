using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.Projects;
using AiRaccoon.Projects;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     D1 through the real <see cref="ToolGate" /> and the real <see cref="ProjectRegistrationGuard" />
///     over an empty registry that throws if anything tries to register: reads under an unregistered
///     id are refused, and only the explicit allow-list (the metrics sentinel, the all-projects
///     promotion listing, the id mint) passes.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReadRegistrationTests
{
    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectIdResolver _resolver = Substitute.For<IProjectIdResolver>();
    private readonly IMetricsReportService _reports = Substitute.For<IMetricsReportService>();
    private readonly FakePromotionQueue _queue = new();
    private readonly ToolGate _gate;

    public ReadRegistrationTests()
    {
        _registry.RegisterAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("a read must never register a project"));
        var guard = new ProjectRegistrationGuard(_registry, NullLogger<ProjectRegistrationGuard>.Instance,
            new NeverMigratedGate());
        _gate = new ToolGate(Substitute.For<IMemoryAccessGuard>(), _queue, new NeverMigratingStore(), guard, _resolver);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Performance_SelfMetricsSentinel_PassesOnAnEmptyRegistry()
    {
        var tools = new PerformanceTools(_reports, _gate);

        await tools.Performance(MetricsConfigKeys.SelfMetricsProjectId, cancellationToken: Ct);

        await _reports.Received(1).GetReportAsync(MetricsConfigKeys.SelfMetricsProjectId,
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan?>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Performance_SentinelCaseVariant_IsRefused()
    {
        var tools = new PerformanceTools(_reports, _gate);

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            tools.Performance("__SELF_METRICS__", cancellationToken: Ct));
    }

    [Fact]
    public async Task Performance_UnregisteredGuid_IsRefused()
    {
        var tools = new PerformanceTools(_reports, _gate);

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            tools.Performance(Guid.CreateVersion7().ToString("D"), cancellationToken: Ct));
    }

    [Fact]
    public async Task Performance_OtherDoubleUnderscoreId_IsRefused()
    {
        var tools = new PerformanceTools(_reports, _gate);

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            tools.Performance("__other__", cancellationToken: Ct));
    }

    [Fact]
    public async Task Search_UnderSelfMetricsSentinel_IsRefused()
    {
        var settings = new InMemorySettings();
        var store = Substitute.For<IMemoryStore>();
        var tools = new MemoryTools(store, _gate,
            new SearchDispatcher(store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(settings), new MemoryWriteService(store, new FakePromotionQueue()),
            NoOpMeasurementRecorder.Instance, settings, NullLogger<MemoryTools>.Instance, new CountingEmbeddingService());

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            tools.Search(MetricsConfigKeys.SelfMetricsProjectId, "anything", sessionId: "sess", cancellationToken: Ct));
    }

    [Fact]
    public async Task PromotionList_AllProjects_PassesOnAnEmptyRegistry()
    {
        var tools = new PromotionTools(_queue, _gate);

        await tools.List(allProjects: true, cancellationToken: Ct);

        _queue.LastListProject.ShouldBeNull();
        await _registry.DidNotReceive().IsRegisteredAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProjectIdTokenGet_PassesOnAnEmptyRegistry()
    {
        var registry = Substitute.For<IProjectRegistry>();
        var tools = new ProjectTools(registry, _gate);

        var minted = await tools.Get(cancellationToken: Ct);

        await registry.Received(1).RegisterAsync(minted.Data!.ProjectId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShareExtractPropose_UnregisteredId_IsRefusedAndQueuesNothing()
    {
        var runner = Substitute.For<ISharedExtractionRunner>();
        var queue = new FakePromotionQueue();
        var tools = new ShareTools(Substitute.For<IMemoryStore>(), _gate,
            new ShareExtractService(Substitute.For<IMemoryStore>(), runner, queue));

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            tools.ShareExtract([Guid.CreateVersion7().ToString("D")], cancellationToken: Ct));

        runner.ReceivedCalls().ShouldBeEmpty();
        queue.LastProject.ShouldBeNull("propose under a refused id must queue nothing");
    }

    [Fact]
    public async Task UnregisteredRefusal_NamesTheLookupTheMintAndTheRegisterVerb()
    {
        var id = Guid.CreateVersion7().ToString("D");

        var ex = await Should.ThrowAsync<UnregisteredProjectException>(() =>
            _gate.RequireAsync(id, AccessRequirement.Read, "memory_search", Ct));

        ex.Message.ShouldContain("project_id_get");
        ex.Message.ShouldContain("project_id_token_get");
        ex.Message.ShouldContain($"'ai-raccoon project id register {id}'");
        ex.Message.ShouldNotContain("before writing");
    }

    [Fact]
    public async Task BlankId_ResolvedToUnregistered_IsRefused()
    {
        var id = Guid.CreateVersion7().ToString("D");
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new ProjectIdResolution.Resolved(id));

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            _gate.RequireAsync(null, AccessRequirement.Read, "memory_search", Ct));
    }

    [Fact]
    public async Task BlankId_ResolvedToRegistered_Passes()
    {
        var id = Guid.CreateVersion7().ToString("D");
        _resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new ProjectIdResolution.Resolved(id));
        _registry.IsRegisteredAsync(id, Arg.Any<CancellationToken>()).Returns(true);

        var canonical = await _gate.RequireAsync(null, AccessRequirement.Read, "memory_search", Ct);

        canonical.ShouldBe(id);
    }
}
