using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     QualityTools stays thin: gate check, service call, envelope wrap — nothing else
///     (mcp-thin). P4 pins the servedRank passthrough: the tool forwards the rank verbatim
///     and defaults it to null when the caller never saw one.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class QualityToolsTests
{
    private readonly FakeToolGate _gate = new();
    private readonly CapturingQualityService _quality = new();
    private readonly QualityTools _tools;

    public QualityToolsTests()
    {
        _tools = new QualityTools(_quality, _gate);
    }

    [Fact]
    public async Task RecordFollowThrough_ForwardsServedRankVerbatim()
    {
        await _tools.RecordFollowThrough("proj-a", "corr-1", "/file.md", servedRank: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastCorrelationId.ShouldBe("corr-1");
        _quality.LastFilePath.ShouldBe("/file.md");
        _quality.LastServedRank.ShouldBe(3);
    }

    [Fact]
    public async Task RecordFollowThrough_OmittedRank_PassesNull()
    {
        await _tools.RecordFollowThrough("proj-a", "corr-1", "/file.md",
            cancellationToken: TestContext.Current.CancellationToken);

        _quality.LastServedRank.ShouldBeNull();
    }

    [Fact]
    public async Task RecordFollowThrough_RequiresWriteAccess_ForTheCallingProject()
    {
        await _tools.RecordFollowThrough("acme", "corr-1", "/file.md",
            cancellationToken: TestContext.Current.CancellationToken);

        _gate.LastProjectId.ShouldBe("acme");
        _gate.LastRequirement.ShouldBe(AccessRequirement.Write);
        _gate.LastToolName.ShouldBe("memory_record_followthrough");
    }

    private sealed class FakeToolGate : IToolGate
    {
        public string? LastProjectId { get; private set; }

        public AccessRequirement? LastRequirement { get; private set; }

        public string? LastToolName { get; private set; }

        public Task RequireBankAvailableAsync(string toolName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken)
        {
            LastProjectId = projectId;
            LastRequirement = requirement;
            LastToolName = toolName;
            return Task.FromResult(projectId ?? string.Empty);
        }

        public Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken) =>
            Task.FromResult(new ApiEnvelope<T>(data, new PromotionMeta(0, null)));
    }

    /// <summary>Dumb record-and-return quality spy: captures the follow-through call, nothing more.</summary>
    private sealed class CapturingQualityService : ISearchQualityService
    {
        public string? LastCorrelationId { get; private set; }

        public string? LastFilePath { get; private set; }

        public int? LastServedRank { get; private set; }

        public Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task RecordSearchAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordSearchSafeAsync(string correlationId, string query, string? scope, string? projectId,
            string kind, string sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RecordFollowThroughAsync(string correlationId, string filePath, int? servedRank = null,
            CancellationToken ct = default)
        {
            LastCorrelationId = correlationId;
            LastFilePath = filePath;
            LastServedRank = servedRank;
            return Task.CompletedTask;
        }

        public Task RecordGradeAsync(string projectId, string correlationId, int grade, string? note,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset from,
            CancellationToken ct = default) =>
            Task.FromResult(new SearchQualityMetrics(0, 0, 0, 0, 0, 0, 0));
    }
}
