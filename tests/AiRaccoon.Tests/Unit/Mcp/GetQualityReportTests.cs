using System.Reflection;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Tools;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     get_quality_report: the read half of the quality surface. The tool is thin (gate check,
///     service call, envelope wrap — mcp-thin), so what is pinned here is what it asks for and
///     what it gives back: the canonical id, the read access requirement, the window it passes,
///     the metrics it returns — and that it records nothing while it reads.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class GetQualityReportTests
{
    private readonly FakeToolGate _gate = new();
    private readonly RecordingQualityService _quality = new();
    private readonly QualityTools _tools;

    public GetQualityReportTests()
    {
        _tools = new QualityTools(_quality, _gate);
    }

    /// <summary>The registered name and the declared scope, read off the method itself.</summary>
    [Fact]
    public void Tool_IsRegisteredUnderGetQualityReport_AndDeclaresTheAllScope()
    {
        var method = typeof(QualityTools).GetMethod(nameof(QualityTools.GetQualityReport));
        method.ShouldNotBeNull("get_quality_report must exist as a tool method");

        var attribute = method!.GetCustomAttribute<McpServerToolAttribute>();
        attribute.ShouldNotBeNull("the method must carry the MCP tool attribute");
        attribute!.Name.ShouldBe("get_quality_report");

        var scope = method.GetCustomAttribute<McpToolScopeAttribute>();
        scope.ShouldNotBeNull("every tool declares its scope — a missing one is a build error (ADR-0139)");
        scope!.Scope.ShouldBe(ToolScope.All, "the report is a telemetry read, like its sibling get_engine_statistics");
    }

    /// <summary>The gate decides the project and the access requirement; the tool only asks.</summary>
    [Fact]
    public async Task Tool_RequiresReadAccess_AndNamesItselfToTheGate()
    {
        await _tools.GetQualityReport("acme", from: null, cancellationToken: TestContext.Current.CancellationToken);

        _gate.LastProjectId.ShouldBe("acme");
        _gate.LastRequirement.ShouldBe(AccessRequirement.Read);
        _gate.LastToolName.ShouldBe("get_quality_report");
    }

    /// <summary>The service sees the canonical id the gate resolved, not the id the caller passed.</summary>
    [Fact]
    public async Task Tool_PassesTheGateCanonicalId_ToTheService()
    {
        _gate.Canonical = "winner";

        await _tools.GetQualityReport("loser", from: null, cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastProjectId.ShouldBe("winner");
    }

    /// <summary>The metrics the service produced are the metrics the caller gets.</summary>
    [Fact]
    public async Task Tool_ReturnsTheServiceMetrics()
    {
        _quality.Metrics = new SearchQualityMetrics(11, 5, 4, 4.25, 0.45, 0.8, 2);

        var envelope = await _tools.GetQualityReport("acme", from: null,
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data.ShouldNotBeNull();
        envelope.Data.TotalSearches.ShouldBe(11);
        envelope.Data.AverageGrade.ShouldBe(4.25);
        envelope.Data.Coverage.ShouldBe(0.8);
    }

    /// <summary>No window given: the tool leaves the default to the service.</summary>
    [Fact]
    public async Task Tool_WithoutAWindow_PassesNoStartToTheService()
    {
        await _tools.GetQualityReport("acme", from: null, cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastFrom.ShouldBeNull("the default window belongs to the service");
    }

    /// <summary>A window given: the tool passes exactly that instant.</summary>
    [Fact]
    public async Task Tool_WithAWindow_PassesThatInstantVerbatim()
    {
        var from = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        await _tools.GetQualityReport("acme", from: from, cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastFrom.ShouldBe(from);
    }

    /// <summary>A report reads; it never records. A quality row created by a read would corrupt the metric.</summary>
    [Fact]
    public async Task Tool_RecordsNothing()
    {
        await _tools.GetQualityReport("acme", from: null, cancellationToken: TestContext.Current.CancellationToken);

        _quality.RecordSearchCalls.ShouldBe(0, "a report must not create a search row");
        _quality.RecordFollowThroughCalls.ShouldBe(0, "a report must not record a follow-through");
        _quality.RecordGradeCalls.ShouldBe(0, "a report must not record a grade");
    }

    private sealed class FakeToolGate : IToolGate
    {
        public string? LastProjectId { get; private set; }

        public AccessRequirement? LastRequirement { get; private set; }

        public string? LastToolName { get; private set; }

        /// <summary>When set, the gate answers this canonical id instead of echoing the argument.</summary>
        public string? Canonical { get; set; }

        public Task RequireBankAvailableAsync(string toolName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken)
        {
            LastProjectId = projectId;
            LastRequirement = requirement;
            LastToolName = toolName;
            return Task.FromResult(Canonical ?? projectId ?? string.Empty);
        }

        public Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken) =>
            Task.FromResult(new ApiEnvelope<T>(data, new PromotionMeta(0, null)));
    }

    /// <summary>Captures what the tool asks the service for, and counts every write it tries to make.</summary>
    private sealed class RecordingQualityService : ISearchQualityService
    {
        public SearchQualityMetrics Metrics { get; set; } = new(0, 0, 0, 0, 0, 0, 0);

        public string? LastProjectId { get; private set; }

        public DateTimeOffset? LastFrom { get; private set; }

        public int RecordSearchCalls { get; private set; }

        public int RecordFollowThroughCalls { get; private set; }

        public int RecordGradeCalls { get; private set; }

        public Task<SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset? from,
            CancellationToken cancellationToken = default)
        {
            LastProjectId = projectId;
            LastFrom = from;
            return Task.FromResult(Metrics);
        }

        public Task RecordSearchAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default,
            IReadOnlyList<RetrievalEvidence>? evidence = null,
        AiRaccoon.Core.Memory.EntryType? entryTypes = null)
        {
            RecordSearchCalls++;
            return Task.CompletedTask;
        }

        public Task RecordSearchSafeAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default,
            IReadOnlyList<RetrievalEvidence>? evidence = null,
        AiRaccoon.Core.Memory.EntryType? entryTypes = null) =>
            Task.CompletedTask;

        public Task<bool> RecordFollowThroughAsync(string projectId, string correlationId, string filePath,
            int? servedRank = null, CancellationToken ct = default)
        {
            RecordFollowThroughCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> RecordGradeAsync(string projectId, string correlationId, int grade, string? note,
            CancellationToken ct = default)
        {
            RecordGradeCalls++;
            return Task.FromResult(true);
        }

        public Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
