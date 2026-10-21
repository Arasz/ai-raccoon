using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Projects;

/// <summary>Turns shared project policy decisions into caller-facing refusals.</summary>
public static class SharedProjectScopeExtensions
{
    extension(ISharedProjectScope scope)
    {
        /// <summary>Throws when the search shape is unsupported for the reserved shared target.</summary>
        public void EnsureCanExecuteInSharedScope(SearchQuery query)
        {
            if (!scope.CanExecuteInSharedScope(query))
            {
                throw new ArgumentException("shared-project-unsupported: search shared with scope all or shared, without a workspace or label.");
            }
        }

        /// <summary>Throws when a note write names the reserved shared target.</summary>
        public void EnsureCanExecuteInSharedScope(MemoryWriteRequest request)
        {
            if (!scope.CanExecuteInSharedScope(request))
            {
                throw new ArgumentException(OrdinaryProjectRequiredMessage("note writes"));
            }
        }

        /// <summary>Throws when an ordinary-only operation names the reserved shared target.</summary>
        public void EnsureCanExecuteInSharedScope(string projectId, string operation)
        {
            if (scope.IsReserved(projectId))
            {
                throw new ArgumentException(OrdinaryProjectRequiredMessage(operation));
            }
        }
    }

    /// <summary>The refusal text for an operation that requires an ordinary project.</summary>
    public static string OrdinaryProjectRequiredMessage(string operation) =>
        $"shared-project-unsupported: {operation} requires an ordinary project; shared supports document ingestion and watches.";
}
