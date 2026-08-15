using AiRaccoon.Core.Metrics;
using System.Text.Json;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     PerformanceTools stays thin: gate check, service call, envelope wrap — nothing else
///     (ADR-0065, mcp-thin; docs/plans/2026-08-15-performance-metrics-implementation.md, WP6). A
///     fake IToolGate/IMetricsReportService keeps this a true unit test of the mapping, not a
///     re-test of ToolGate's own access-mode resolution (already covered elsewhere).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PerformanceToolsTests
{
    private readonly FakeToolGate _gate = new();
    private readonly FakeMetricsReportService _reportService = new();
    private readonly PerformanceTools _tools;

    public PerformanceToolsTests()
    {
        _tools = new PerformanceTools(_reportService, _gate);
    }

    [Fact]
    public async Task Performance_RequiresReadAccess_ForTheCallingProject()
    {
        await _tools.Performance("acme", cancellationToken: TestContext.Current.CancellationToken);

        _gate.LastProjectId.ShouldBe("acme");
        _gate.LastRequirement.ShouldBe(AccessRequirement.Read);
        _gate.LastToolName.ShouldBe("memory_performance");
    }

    [Fact]
    public async Task Performance_PassesTheDerivedToolInventory_ToTheService()
    {
        await _tools.Performance("acme", cancellationToken: TestContext.Current.CancellationToken);

        _reportService.LastToolNames.ShouldBe(McpToolInventory.Names());
    }

    [Fact]
    public async Task Performance_NoWindowOrBucket_PassesNullsThrough()
    {
        await _tools.Performance("acme", cancellationToken: TestContext.Current.CancellationToken);

        _reportService.LastWindow.ShouldBeNull();
        _reportService.LastBucket.ShouldBeNull();
    }

    [Fact]
    public async Task Performance_WindowAndBucketMinutes_ConvertToTimeSpans()
    {
        await _tools.Performance("acme", windowMinutes: 60, bucketMinutes: 5,
            cancellationToken: TestContext.Current.CancellationToken);

        _reportService.LastWindow.ShouldBe(TimeSpan.FromMinutes(60));
        _reportService.LastBucket.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Performance_WrapsTheReportInTheEnvelope()
    {
        var result = await _tools.Performance("acme", cancellationToken: TestContext.Current.CancellationToken);

        result.Data.ShouldBe(_reportService.ReportToReturn);
    }

    /// <summary>AC1: an agent calling memory_performance gets JSON, not prose.</summary>
    [Fact]
    public async Task Performance_ResponseParsesAsJson_AndCarriesASeriesPerTool()
    {
        var result = await _tools.Performance("acme", cancellationToken: TestContext.Current.CancellationToken);

        var json = JsonSerializer.Serialize(result);
        using var parsed = JsonDocument.Parse(json);

        // Plain JsonSerializer.Serialize (no options) uses the record's own PascalCase property
        // names, not the SDK wire's camelCase (McpToolContractTests exercises that path instead).
        parsed.RootElement.GetProperty("Data").GetProperty("Series").GetArrayLength()
            .ShouldBe(_reportService.ReportToReturn.Series.Count);
    }

    private sealed class FakeToolGate : IToolGate
    {
        public string? LastProjectId { get; private set; }
        public AccessRequirement? LastRequirement { get; private set; }
        public string? LastToolName { get; private set; }

        public Task RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken)
        {
            LastProjectId = projectId;
            LastRequirement = requirement;
            LastToolName = toolName;
            return Task.CompletedTask;
        }

        public Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken) =>
            Task.FromResult(new ApiEnvelope<T>(data, new PromotionMeta(0, null)));
    }

    private sealed class FakeMetricsReportService : IMetricsReportService
    {
        public IReadOnlyList<string>? LastToolNames { get; private set; }
        public TimeSpan? LastWindow { get; private set; }
        public TimeSpan? LastBucket { get; private set; }

        public PerformanceReport ReportToReturn { get; } =
            new(DateTimeOffset.UtcNow, TimeSpan.FromHours(3), TimeSpan.FromMinutes(1), 180, []);

        public Task<PerformanceReport> GetReportAsync(string projectId, IReadOnlyList<string> toolNames,
            TimeSpan? window, TimeSpan? bucket, CancellationToken cancellationToken = default)
        {
            LastToolNames = toolNames;
            LastWindow = window;
            LastBucket = bucket;
            return Task.FromResult(ReportToReturn);
        }
    }
}
