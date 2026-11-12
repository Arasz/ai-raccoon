namespace AiRaccoon.Infrastructure.Sqlite.Decisions;

/// <summary>A project a repair plan would move or delete, with the graph tables it owns rows in.</summary>
public sealed record DecisionGraphOwner(string ProjectId, IReadOnlyList<string> Tables);

/// <summary>Project repair cannot move or retire an owner of native decision graph data; names every occupied owner.</summary>
public sealed class DecisionGraphRepairException(IReadOnlyList<DecisionGraphOwner> owners)
    : InvalidOperationException(
        $"decision-graph-repair-blocked: {string.Join("; ", owners.Select(owner => $"project '{owner.ProjectId}' owns rows in {string.Join(", ", owner.Tables)}"))}. Native graph merging is not supported; every owner remains unchanged.")
{
    public IReadOnlyList<DecisionGraphOwner> Owners { get; } = owners;
}
