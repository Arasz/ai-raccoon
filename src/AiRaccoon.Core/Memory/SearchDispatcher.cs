using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.SearchQuality;

namespace AiRaccoon.Core.Memory;

/// <inheritdoc cref="ISearchDispatcher" />
public sealed class SearchDispatcher(IMemoryStore store, ICodeSearchService codeSearch, ISearchQualityService qualityService)
    : ISearchDispatcher
{
    public async Task<SearchDispatchResult> DispatchAsync(SearchQuery searchQuery, SearchKind kind, string rawScope,
        string correlationId, string sessionId, int? codeLimit = null, double? codeMinRelativeScore = null,
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
                        codeLimit ?? searchQuery.Limit, codeMinRelativeScore ?? searchQuery.MinRelativeScore,
                        searchQuery.RrfK, searchQuery.FtsWeight, searchQuery.VectorWeight, searchQuery.CandidateWindow),
                    cancellationToken);
                codeResults = codeSearchResults.Results;
                codeWarning = codeSearchResults.Warning;
            }
        }

        // ADR-0094: every kind records. The pre-0094 exclusion (code/both never record)
        // designed the quality signal away from the default path the day PR #580 flipped the
        // default kind to both (rows stop Aug 24; hermes-default ran 307 searches with 0 rows).
        // Privacy shape: the row describes the memory leg for memory/both (code paths are never
        // stored -- code_entries never leaves the machine per ADR-0085, and search_quality never
        // leaves it either -- StripNonSyncableAsync DROPs it from every pushed snapshot per ADR-0098); a code
        // search records its code result count with an empty file list, so grades stay
        // interpretable without storing code paths. The shared query text is stored as-is: a
        // memory query can already carry identifiers, so a code-adjacent query is the same leak
        // class memory rows already accept, not a new one.
        var (qualityCount, qualityFiles) = kind == SearchKind.Code
            ? (codeResults?.Count ?? 0, (IReadOnlyList<string>)[])
            : (results.Count, [.. results.Where(r => r.SourceFile is not null).Select(r => r.SourceFile!).Take(5)]);
        // P6a threading: join the served rows to the P4 sidecar by hash, in served order.
        // Null sidecar stays null (absent-evidence writes exactly as before); a present sidecar
        // yields the served subset only, so the payload is bounded by the served rows.
        IReadOnlyList<RetrievalEvidence>? evidence = null;
        if (memorySearchResults?.EvidenceByHash is not null)
        {
            var byHash = memorySearchResults.EvidenceByHash;
            var joined = new List<RetrievalEvidence>(results.Count);
            foreach (var row in results)
            {
                if (byHash.TryGetValue(row.Hash, out var rowEvidence))
                {
                    joined.Add(rowEvidence);
                }
            }

            evidence = joined;
        }

        await qualityService.RecordSearchSafeAsync(correlationId: correlationId, query: searchQuery.Query,
            scope: rawScope, projectId: searchQuery.ProjectId, kind: kind.ToString().ToLowerInvariant(),
            sessionId: sessionId, resultCount: qualityCount, topSourceFiles: qualityFiles,
            ct: cancellationToken, evidence: evidence);

        return new SearchDispatchResult(results, memorySearchResults, codeResults, codeWarning);
    }
}
