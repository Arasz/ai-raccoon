using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.SearchQuality;

namespace AiRaccoon.Core.Memory;

/// <inheritdoc cref="ISearchDispatcher" />
public sealed class SearchDispatcher(IMemoryStore store, ICodeSearchService codeSearch, ISearchQualityService qualityService)
    : ISearchDispatcher
{
    public async Task<SearchDispatchResult> DispatchAsync(SearchQuery searchQuery, SearchKind kind, string rawScope,
        string correlationId, int? codeLimit = null, double? codeMinRelativeScore = null,
        CancellationToken cancellationToken = default)
    {
        SearchResults? memorySearchResults = null;
        IReadOnlyList<MemorySearchResult> results = [];
        if (kind != SearchKind.Code)
        {
            memorySearchResults = await store.SearchAsync(searchQuery, cancellationToken);
            results = memorySearchResults.Results;
        }

        IReadOnlyList<CodeSearchResult>? codeResults = null;
        string? codeWarning = null;
        if (kind != SearchKind.Memory)
        {
            // Code is project-scoped only (§3.1): an explicit scope=shared request never
            // contributes code rows, even under kind=code/both.
            if (searchQuery.Scope == SearchScope.Shared)
            {
                codeResults = [];
            }
            else
            {
                var codeSearchResults = await codeSearch.SearchAsync(
                    new CodeSearchQuery(searchQuery.ProjectId, searchQuery.Query,
                        codeLimit ?? searchQuery.Limit, codeMinRelativeScore ?? searchQuery.MinRelativeScore),
                    cancellationToken);
                codeResults = codeSearchResults.Results;
                codeWarning = codeSearchResults.Warning;
            }
        }

        // search_quality exclusion (§1/§3.6): a code/both query never records -- the recorder's
        // rows sync, and recording a code-adjacent query would leak identifiers/paths off-machine.
        if (kind == SearchKind.Memory)
        {
            await qualityService.RecordSearchSafeAsync(correlationId, searchQuery.Query, rawScope, searchQuery.ProjectId,
                results.Count, [.. results.Where(r => r.SourceFile is not null).Select(r => r.SourceFile!).Take(5)],
                cancellationToken);
        }

        return new SearchDispatchResult(results, memorySearchResults, codeResults, codeWarning);
    }
}
