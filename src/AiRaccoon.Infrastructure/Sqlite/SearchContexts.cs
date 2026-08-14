using AiRaccoon.Core.Memory;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>The bank contexts a search query reads, per scope (see docs/work/features-agent-memory/spec-issue-1.md §4.1): one memory_search query per in-scope context.</summary>
internal static class SearchContexts
{
    /// <summary>
    ///     <see cref="For" /> with the project's labels read from the bank — the labels are only
    ///     needed when no <see cref="SearchQuery.ContextLabel" /> narrows the search, so the read is
    ///     skipped otherwise.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveAsync(SqliteConnection connection,
        SearchQuery query, CancellationToken cancellationToken)
    {
        var needsLabels = query.Scope is SearchScope.All or SearchScope.Project
                          && string.IsNullOrWhiteSpace(query.ContextLabel);
        var labels = needsLabels
            ? (await connection.QueryAsync<string>(new CommandDefinition(MemorySql.CustomContextLabels,
                new { projectId = query.ProjectId }, cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToList()
            : [];
        return For(query, labels);
    }

    /// <param name="customLabels">
    ///     The project's custom-context labels. The project is the isolation boundary; a context is
    ///     a label inside it, not a second boundary. No <see cref="SearchQuery.ContextLabel" />
    ///     means every context in the project; one means that context only (docs/adr/0045).
    /// </param>
    public static IReadOnlyList<string> For(SearchQuery query, IReadOnlyList<string>? customLabels = null)
    {
        var contexts = new List<string>();

        if (query.Scope is SearchScope.All or SearchScope.Shared)
        {
            contexts.Add(ContextNaming.SharedContext);
        }

        if (query.Scope is SearchScope.All or SearchScope.Project)
        {
            if (string.IsNullOrWhiteSpace(query.ContextLabel))
            {
                contexts.Add(ContextNaming.ProjectContext(query.ProjectId));
                foreach (var label in customLabels ?? [])
                {
                    contexts.Add(ContextNaming.LabelContext(query.ProjectId, label));
                }
            }
            else
            {
                // Narrowing, not augmenting: the caller asked for that context, so the project's
                // unlabelled rows are out of scope too.
                contexts.Add(ContextNaming.LabelContext(query.ProjectId, query.ContextLabel));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.WorkspaceId) && query.Scope is not SearchScope.Shared)
        {
            contexts.Add(ContextNaming.WorkspaceContext(query.WorkspaceId));
        }

        return contexts;
    }
}
