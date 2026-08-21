namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Corpus classification for one file (docs/work/2026-08-21-code-search-implementation-plan.md
///     §3.4). Memory wins on overlap — unreachable given the two registries are disjoint by test,
///     kept as the runtime rule for future drift.
/// </summary>
public static class IngestDispatcher
{
    public static CorpusKind Classify(IFileTypeMatcher memoryMatcher, ICodeFileTypeMatcher codeMatcher, string path)
    {
        if (memoryMatcher.TryGetHandler(path, out _))
        {
            return CorpusKind.Memory;
        }

        return codeMatcher.IsCodeFile(path) ? CorpusKind.Code : CorpusKind.Neither;
    }
}
