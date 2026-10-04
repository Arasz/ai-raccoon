namespace AiRaccoon.Core.Projects;

/// <summary>Several registered projects share this exact name; the MCP tools map this to <c>project-name-ambiguous</c>.</summary>
public sealed class ProjectNameAmbiguousException(string name, IReadOnlyList<string> projectIds)
    : InvalidOperationException(
        $"{projectIds.Count} projects are registered under the name '{name}': {string.Join(", ", projectIds)}. " +
        "Ask the user which one to use and pass that id as projectId.")
{
    /// <summary>Every id registered under the name, in lookup order.</summary>
    public IReadOnlyList<string> ProjectIds { get; } = projectIds;
}
