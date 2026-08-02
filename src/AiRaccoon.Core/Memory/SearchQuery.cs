using AiRaccoon.Core.Common;

namespace AiRaccoon.Core.Memory;

public sealed record SearchQuery
{
    public SearchQuery(
        string projectId,
        string query,
        SearchScope scope = SearchScope.All,
        string? workspaceId = null,
        int limit = 20,
        double minScore = 0.7)
    {
        Guard.NotNullOrWhiteSpace(projectId, nameof(projectId));
        Guard.NotNullOrWhiteSpace(query, nameof(query));
        Guard.GreaterThan(limit, 0, nameof(limit));
        Guard.InRange(minScore, 0.0, 1.0, nameof(minScore));

        ProjectId = projectId;
        Query = query;
        Scope = scope;
        WorkspaceId = workspaceId;
        Limit = limit;
        MinScore = minScore;
    }

    public string ProjectId { get; }

    public string Query { get; }

    public SearchScope Scope { get; }

    public string? WorkspaceId { get; }

    public int Limit { get; }

    public double MinScore { get; }
}
