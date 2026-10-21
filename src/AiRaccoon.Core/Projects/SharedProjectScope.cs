using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Projects;

/// <summary>The one implementation of <see cref="ISharedProjectScope" />: the reserved <c>shared</c> target.</summary>
public sealed class SharedProjectScope : ISharedProjectScope
{
    /// <summary>The reserved, case-sensitive project id.</summary>
    public const string Id = "shared";

    /// <summary>The machine-local marker that says the reserved target owns the shared watch rows.</summary>
    public const string WatchActivationKey = "shared.documents.watch-owner";

    /// <inheritdoc />
    public bool IsReserved(string? projectId) => IsReservedId(projectId);

    /// <summary>True when the id names the reserved shared target — the one id check, for the call sites that hold no scope.</summary>
    public static bool IsReservedId(string? projectId) => string.Equals(projectId, Id, StringComparison.Ordinal);

    /// <inheritdoc />
    public string ContextFor(string projectId) => IsReserved(projectId)
        ? ContextNaming.SharedContext
        : ContextNaming.ProjectContext(projectId);

    /// <inheritdoc />
    public void RequireSearch(SearchQuery query)
    {
        if (IsReserved(query.ProjectId) && (query.Scope == SearchScope.Project
            || !string.IsNullOrWhiteSpace(query.WorkspaceId) || !string.IsNullOrWhiteSpace(query.ContextLabel)))
        {
            throw new ArgumentException("shared-project-unsupported: search shared with scope all or shared, without a workspace or label.");
        }
    }

    /// <inheritdoc />
    public void RequireOrdinary(string projectId, string operation)
    {
        if (IsReserved(projectId))
        {
            throw new ArgumentException($"shared-project-unsupported: {operation} requires an ordinary project; shared supports document ingestion and watches.");
        }
    }
}
