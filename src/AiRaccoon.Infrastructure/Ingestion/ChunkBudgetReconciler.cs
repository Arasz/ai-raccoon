using System.Globalization;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>What a chunk-budget rebudget pass found and did. A skip leaves its group untouched.</summary>
/// <param name="Budget">The resolved chunk budget the pass re-chunked at.</param>
/// <param name="NoteGroupsRechunked">Note groups replaced with new pieces from their proven merge.</param>
/// <param name="MirrorGroupsRechunked">File-row groups re-ingested from their source file on disk.</param>
/// <param name="GroupsUnchanged">Groups whose rows already reproduce at the resolved budget.</param>
/// <param name="RetryableSkipped">Note groups no join proves — untouched, retried on a later pass.</param>
/// <param name="TerminalSkipped">Mirror groups whose source file is gone — nothing re-chunkable exists.</param>
public sealed record ChunkRebudgetReport(
    int Budget,
    int NoteGroupsRechunked,
    int MirrorGroupsRechunked,
    int GroupsUnchanged,
    int RetryableSkipped,
    int TerminalSkipped);

/// <summary>The bank's chunk-budget state against the resolved engine budget — the drift trigger's verdict.</summary>
/// <param name="Matches">True when the <c>embedding.chunkBudget</c> stamp already equals the resolved budget.</param>
/// <param name="BankIsEmpty">True when the bank holds no rows, so a stale or absent stamp is stamped silently.</param>
/// <param name="ResolvedBudget">The chunk budget the configured engine resolves to right now.</param>
public sealed record ChunkBudgetState(bool Matches, bool BankIsEmpty, int ResolvedBudget);

/// <summary>
///     config-D P1: re-chunks existing bank rows at the resolved chunk budget and keeps the
///     <c>embedding.chunkBudget</c> stamp honest. Runs inside the model-migration drain, before any
///     embed — the phase the budget change migrates banks by. Note groups re-chunk from their
///     <see cref="ChunkReassembly" />-proven merge; mirror groups re-chunk from disk.
/// </summary>
public interface IChunkBudgetReconciler
{
    /// <summary>Read-only drift check: how the stored stamp compares to the resolved budget.</summary>
    Task<ChunkBudgetState> CheckAsync(SqliteConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Re-chunks every group at the resolved budget and stamps it when zero groups were retryable-skipped.</summary>
    Task<ChunkRebudgetReport> RunAsync(SqliteConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>
///     The chunk-budget rebudget pass. Replacement is one transaction per group (tombstone, delete,
///     insert <c>pending</c>) carrying the insert spec forward from the group's rows —
///     <c>path</c>, <c>source_file</c>, <c>section</c>, the scope/context/workspace keys,
///     <c>source_id</c>, <c>agent_id</c>, <c>created_at</c> — never <c>ChunkBackfill</c>'s nulls.
///     Insert order is text order (ADR-0122's write convention), which keeps
///     <see cref="NoteTextOrder" />'s id-order candidate working. Plain notes keep
///     <c>WriteChunks</c>' -1/0 sentinel; citing notes take positions from the group-scoped
///     keeping-order repair — never <c>RecomputeChunkColumnsBankWide</c>, whose mixed-partition fill
///     duplicates kept file positions (ADR-0123). Per-row rating/access metadata is the accepted
///     loss of any re-chunk.
/// </summary>
public sealed class ChunkBudgetReconciler(
    IFileTypeMatcher fileTypeMatcher,
    IMarkdownChunker noteChunker,
    IEmbeddingService embeddingService,
    TimeProvider timeProvider,
    Func<IMemoryStore> memoryStore) : IChunkBudgetReconciler
{
    /// <inheritdoc />
    public async Task<ChunkBudgetState> CheckAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        var budget = await new ChunkPositionScanner(fileTypeMatcher, embeddingService)
            .BudgetAsync(connection, cancellationToken);
        var stamp = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.ChunkBudget }, cancellationToken: cancellationToken));
        if (int.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamped)
            && stamped == budget.MaxTokens)
        {
            return new ChunkBudgetState(true, false, budget.MaxTokens);
        }

        var rowsExist = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM entries)", cancellationToken: cancellationToken)) > 0;
        return rowsExist
            ? new ChunkBudgetState(false, false, budget.MaxTokens)
            : new ChunkBudgetState(false, true, budget.MaxTokens);
    }

    /// <inheritdoc />
    public async Task<ChunkRebudgetReport> RunAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        var scanner = new ChunkPositionScanner(fileTypeMatcher, embeddingService);
        var budget = await scanner.BudgetAsync(connection, cancellationToken);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var notesRechunked = 0;
        var mirrorsRechunked = 0;
        var unchanged = 0;
        var retryable = 0;
        var terminal = 0;
        List<string> citingPaths = [];

        foreach (var group in await connection.QueryAsync<NoteGroup>(new CommandDefinition(
                     MemorySql.SelectRebudgetNoteGroups, cancellationToken: cancellationToken)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = (await connection.QueryAsync<GroupRow>(new CommandDefinition(
                MemorySql.SelectRebudgetGroupRows,
                new
                {
                    path = group.Path, scope = group.Scope, projectId = group.ProjectId,
                    contextLabel = group.ContextLabel, workspaceId = group.WorkspaceId
                }, cancellationToken: cancellationToken))).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            // The recovery key (plan F8): only a merge proven by sha256 == the path stem is re-chunked.
            var merged = ChunkReassembly.TryMerge(group.Path!,
                [.. rows.Select(row => new NoteRow(row.Id, row.Value, row.ChunkIndex))]);
            if (merged is null)
            {
                retryable++;
                continue;
            }

            var pieces = noteChunker.Chunk(merged, budget.MaxTokens, budget.OverlayTokens, budget.CountTokens);
            var pieceHashes = pieces
                .Select(piece => ContentHash.Of(group.Path!, piece))
                .ToHashSet(StringComparer.Ordinal);
            if (pieceHashes.SetEquals(rows.Select(row => row.Hash)))
            {
                unchanged++;
                continue;
            }

            await ReplaceNoteGroupAsync(connection, group, rows, pieces, pieceHashes, now, cancellationToken);
            notesRechunked++;
            if (rows[0].SourceFile is not null)
            {
                citingPaths.Add(group.Path!);
            }
        }

        foreach (var group in await connection.QueryAsync<MirrorGroup>(new CommandDefinition(
                     MemorySql.SelectRebudgetMirrorGroups, cancellationToken: cancellationToken)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = (await connection.QueryAsync<MirrorRow>(new CommandDefinition(
                MemorySql.SelectRebudgetMirrorRows,
                new { projectId = group.ProjectId, sourceFile = group.SourceFile },
                cancellationToken: cancellationToken))).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            var scan = scanner.Scan(group.SourceFile,
                [.. rows.Select(row => new StoredChunk(row.Id, row.Hash))],
                budget.MaxTokens, budget.OverlayTokens, budget.CountTokens);
            if (!scan.FileUsable)
            {
                // A vanished/unreadable source file has nothing re-chunkable: untouched and terminal.
                terminal++;
                continue;
            }

            if (scan.MatchesStoredRows)
            {
                unchanged++;
                continue;
            }

            // The same unconditional replace the reingest repair uses (scan-and-replace); it chunks
            // from disk at the same resolved budget and leaves the rows pending for the embed drain.
            await memoryStore().ReplaceAsync(group.ProjectId, group.SourceFile,
                WatchDigestExecutor.ComputeHash(group.SourceFile, scan.Content), cancellationToken);
            mirrorsRechunked++;
        }

        if (citingPaths.Count > 0)
        {
            // One keeping-order renumber at phase end, never per-group (review F8): it places each
            // fresh -1 row in its partition, then the replaced citing notes take their proven text order.
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.RecomputeChunkColumnsBankWideKeepingOrder,
                new { lastId = long.MaxValue }, cancellationToken: cancellationToken));
            await new NoteChunkOrderRepair().RunAsync(connection, citingPaths, cancellationToken);
        }

        if (retryable == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new
                {
                    key = EmbeddingSettingsKeys.ChunkBudget,
                    value = budget.MaxTokens.ToString(CultureInfo.InvariantCulture)
                }, cancellationToken: cancellationToken));
        }

        return new ChunkRebudgetReport(budget.MaxTokens, notesRechunked, mirrorsRechunked, unchanged, retryable, terminal);
    }

    /// <summary>
    ///     Swaps one note group for its new pieces in one transaction — tombstone each vanishing hash
    ///     (never one the same replace re-inserts), delete, insert <c>pending</c> in text order with the
    ///     insert spec carried forward — so a failure anywhere leaves the group's old rows whole.
    /// </summary>
    private static async Task ReplaceNoteGroupAsync(SqliteConnection connection, NoteGroup group,
        IReadOnlyList<GroupRow> rows, IReadOnlyList<string> pieces, IReadOnlySet<string> pieceHashes,
        long now, CancellationToken cancellationToken)
    {
        var template = rows[0];
        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken));
        try
        {
            foreach (var row in rows)
            {
                if (!pieceHashes.Contains(row.Hash))
                {
                    await connection.ExecuteAsync(new CommandDefinition(MemorySql.TombstoneFromPredicate("id = @id"),
                        new { id = row.Id, deletedAt = now }, cancellationToken: cancellationToken));
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM entries WHERE id = @id", new { id = row.Id }, cancellationToken: cancellationToken));
            }

            foreach (var piece in pieces)
            {
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.InsertEntry,
                    new
                    {
                        hash = ContentHash.Of(group.Path!, piece),
                        path = group.Path,
                        value = piece,
                        sourceFile = template.SourceFile,
                        section = template.Section,
                        scope = template.Scope,
                        projectId = template.ProjectId,
                        contextLabel = template.ContextLabel,
                        workspaceId = template.WorkspaceId,
                        agentId = template.AgentId,
                        createdAt = template.CreatedAt,
                        updatedAt = now,
                        sourceId = template.SourceId,
                        // Plain notes keep WriteChunks' sentinel; the phase-end repair positions citing notes.
                        chunkIndex = -1,
                        totalChunks = 0
                    }, cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: cancellationToken));
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: CancellationToken.None));
            throw;
        }
    }

    private sealed record NoteGroup(string? Scope, string? ProjectId, string? ContextLabel, string? WorkspaceId,
        string? Path);

    private sealed record GroupRow(long Id, string Hash, string Value, string? SourceFile, string? Section,
        string? Scope, string? ProjectId, string? ContextLabel, string? WorkspaceId, string? AgentId,
        long CreatedAt, long? SourceId, long ChunkIndex);

    private sealed record MirrorGroup(string ProjectId, string SourceFile);

    private sealed record MirrorRow(long Id, string Hash);
}
