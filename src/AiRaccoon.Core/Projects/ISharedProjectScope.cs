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

    /// <summary>True when the query includes shared memory and its project target supports that scope.</summary>
    bool CanExecuteInSharedScope(SearchQuery query);

    /// <summary>True when an ordinary-only operation is allowed; false for the reserved shared target.</summary>
    bool CanExecuteInSharedScope(MemoryWriteRequest request);

    /// <summary>True when the search can include private project code rows.</summary>
    bool CanSearchCode(SearchQuery query);
}
