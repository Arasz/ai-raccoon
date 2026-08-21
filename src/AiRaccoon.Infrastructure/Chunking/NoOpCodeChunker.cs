using AiRaccoon.Core.Chunking;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>
///     Interim `ICodeChunker` (docs/work/2026-08-21-code-search-implementation-plan.md §12.5, Wave
///     3): the real line-range chunker (WP2) is engine-blocked, so production DI registers this
///     until then — every file still routes to the code corpus (matcher/ingest plumbing proven
///     now), it just carries zero chunks. Logs once per process, not once per file.
/// </summary>
public sealed partial class NoOpCodeChunker(ILogger<NoOpCodeChunker> logger) : ICodeChunker
{
    private int _logged;

    public IReadOnlyList<CodeChunk> Chunk(string text)
    {
        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            Log.ChunkerNotYetAvailable(logger);
        }

        return [];
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 420, Level = LogLevel.Information,
            Message = "Code chunking not yet available; code chunks arrive with the code engine wave (returning 0 chunks)")]
        public static partial void ChunkerNotYetAvailable(ILogger logger);
    }
}
