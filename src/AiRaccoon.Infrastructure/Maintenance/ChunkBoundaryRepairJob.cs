using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Runs <see cref="ChunkBoundaryRepair" /> once per bank: it heals rows an older chunker cut mid-word,
///     which the write paths no longer create. Sits before PendingEmbedJob, which embeds what it leaves.
/// </summary>
public sealed partial class ChunkBoundaryRepairJob(
    IFileTypeMatcher fileTypeMatcher,
    IMarkdownChunker noteChunker,
    IPlainTextChunker fallbackChunker,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    TimeProvider timeProvider,
    ILogger<ChunkBoundaryRepairJob> logger) : IMaintenanceJob
{
    public const string JobName = "chunk-boundary-repair-v2";

    public string Name => JobName;

    public string DisplayName => "re-chunk rows an older chunker cut mid-word";

    public TimeSpan? Interval => null;

    public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var report = await new ChunkBoundaryRepair(fileTypeMatcher, noteChunker, fallbackChunker, embeddingService, timeProvider)
            .RunAsync(connection, store, cancellationToken);
        Log.Repaired(logger, report.FilesReingested, report.GroupsRepaired, report.RowsWritten, report.NotesUnproven);
        return report.RowsWritten > 0 || report.FilesReingested > 0;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 447, Level = LogLevel.Information,
            Message = "Chunk boundary repair: {Reingested} file(s) re-ingested, {Groups} group(s) re-chunked into {Rows} row(s), {Unproven} note(s) left as stored (text order not provable from the rows)")]
        public static partial void Repaired(ILogger logger, int reingested, int groups, int rows, int unproven);
    }
}
