using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Projects;

/// <summary>
///     The reserved shared document target's scope rules: the id it owns, the context its rows
///     live in, and the operations it refuses (ADR-0137). One implementation,
///     <see cref="SharedProjectScope" />, wired by the composition root.
/// </summary>
public interface ISharedProjectScope
{
    /// <summary>True when the id names the reserved shared target.</summary>
    bool IsReserved(string? projectId);

    /// <summary>The context the id's rows live in: the shared context, or the id's own project context.</summary>
    string ContextFor(string projectId);

    /// <summary>Refuses the search shapes the reserved target does not support.</summary>
    void RequireSearch(SearchQuery query);

    /// <summary>Checks whether an operation can use an ordinary project.</summary>
    ScopeCheckResult CheckOrdinary(string projectId, string operation);

    /// <summary>Refuses the operations the reserved target does not support.</summary>
    void RequireOrdinary(string projectId, string operation);
}
