using AiRaccoon.Core.Chunking;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>
///     Retained as a zero-chunk `ICodeChunker` stand-in for tests that need to pin the B1
///     fingerprint gate's "no chunks → no fingerprint" behavior (docs/work/2026-08-21-code-search-implementation-plan.md
///     §3.4) — no longer the production registration (that is <c>CodeChunker</c>, WP2). Logs once
///     per process, not once per file.
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
