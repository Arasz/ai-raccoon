using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Runs <see cref="NoteChunkOrderRepair" /> once per bank: it puts source-citing notes' chunk positions in text
///     order, which the write path now does on its own. Sits before ChunkBoundaryRepairJob, which renumbers from them.
/// </summary>
public sealed partial class NoteChunkOrderRepairJob(ILogger<NoteChunkOrderRepairJob> logger) : IMaintenanceJob
{
    public const string JobName = "note-chunk-order-v1";

    public string Name => JobName;

    public string DisplayName => "put source-citing notes' chunk positions in text order";

    public TimeSpan? Interval => null;

    public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var report = await new NoteChunkOrderRepair().RunAsync(connection, cancellationToken);
        Log.Repaired(logger, report.NotesReordered, report.NotesUnproven);
        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 446, Level = LogLevel.Information,
            Message = "Note chunk order: {Reordered} note(s) put in text order, {Unproven} left as stored (text order not provable from the rows)")]
        public static partial void Repaired(ILogger logger, int reordered, int unproven);
    }
}
