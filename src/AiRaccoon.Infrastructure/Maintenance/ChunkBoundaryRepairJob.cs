using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Runs <see cref="ChunkBoundaryRepair" /> once per bank: it heals rows an older chunker cut mid-word,
///     which the write paths no longer create. Sits before PendingEmbedJob, which embeds what it leaves.
/// </summary>
public sealed class ChunkBoundaryRepairJob(
    IFileTypeMatcher fileTypeMatcher,
    IMarkdownChunker noteChunker,
    IPlainTextChunker fallbackChunker,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    TimeProvider timeProvider) : IMaintenanceJob
{
    public const string JobName = "chunk-boundary-repair-v1";

    public string Name => JobName;

    public string DisplayName => "re-chunk rows an older chunker cut mid-word";

    public TimeSpan? Interval => null;

    public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var report = await new ChunkBoundaryRepair(fileTypeMatcher, noteChunker, fallbackChunker, embeddingService, timeProvider)
            .RunAsync(connection, store, cancellationToken);
        return report.RowsWritten > 0 || report.FilesReingested > 0;
    }
}
