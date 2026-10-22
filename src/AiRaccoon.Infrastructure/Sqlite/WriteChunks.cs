using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Row insertion for a chunked `add_memory` (docs/adr/0064). Lives beside
///     <see cref="EntryBucket" /> and <see cref="ContextFilterProvider" /> rather than inside
///     <see cref="Memory.SqliteMemoryStore" />, whose size ratchet says to split rather than raise the cap.
/// </summary>
internal static class WriteChunks
{
    extension(SqliteConnection connection)
    {
        /// <summary>Appends one chunk of an agent write to its group, unless the row is already there.</summary>
        public async Task InsertAsync(string hash, string path, string chunk,
            MemorySource source, MemoryWriteRequest request,
            BucketColumns bucket, long now,
            IEntryKindClassifier entryTypeClassifier,
            CancellationToken cancellationToken)
        {
            var exists = await connection.ExecuteScalarAsync<long?>(
                    new CommandDefinition(MemorySql.EntryExistsByPathAndHashInBucket,
                        new
                        {
                            hash,
                            path,
                            scope = bucket.Scope,
                            projectId = bucket.ProjectId,
                            contextLabel = bucket.ContextLabel,
                            workspaceId = bucket.WorkspaceId
                        }, cancellationToken: cancellationToken)) is not null;
            if (exists)
            {
                return;
            }

            await connection.ExecuteAsync(
                    new CommandDefinition(MemorySql.InsertEntry,
                        new
                        {
                            hash,
                            path,
                            value = chunk,
                            sourceFile = source.SourceLocator is { Length: > 0 } ? source.SourceLocator : null,
                            section = request.Section,
                            scope = bucket.Scope,
                            projectId = bucket.ProjectId,
                            contextLabel = bucket.ContextLabel,
                            workspaceId = bucket.WorkspaceId,
                            agentId = request.AgentId,
                            createdAt = now,
                            updatedAt = now,
                            sourceId = source.Id,
                            // An agent write is a memory, whatever it cites (see the write path).
                            entryType = entryTypeClassifier.ToStoredValue(EntryKind.Memory),
                            // Position unknown here — this is an append to a (ctx, source_file) group, not
                            // a re-chunk of a whole document; RecomputeChunkColumnsForContext fills it in.
                            chunkIndex = -1,
                            totalChunks = 0
                        },
                        cancellationToken: cancellationToken));
        }
    }
}
