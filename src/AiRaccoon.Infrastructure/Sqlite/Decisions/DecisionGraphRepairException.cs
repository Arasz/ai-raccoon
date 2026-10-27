namespace AiRaccoon.Infrastructure.Sqlite.Decisions;

/// <summary>Project repair cannot retire an owner of native decision graph data.</summary>
public sealed class DecisionGraphRepairException(string projectId, IReadOnlyList<string> tables)
    : InvalidOperationException($"decision-graph-repair-blocked: project '{projectId}' owns rows in {string.Join(", ", tables)}. Native graph merging is not supported; both owners remain unchanged.")
{
    public string ProjectId { get; } = projectId;
    public IReadOnlyList<string> Tables { get; } = tables;
}
