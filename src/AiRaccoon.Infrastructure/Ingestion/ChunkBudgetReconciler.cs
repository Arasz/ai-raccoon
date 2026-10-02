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
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>What a chunk-budget rebudget pass found and did. A skip leaves its group untouched.</summary>
/// <param name="Budget">The resolved chunk budget the pass re-chunked at.</param>
/// <param name="NoteGroupsRechunked">Note groups replaced with new pieces from their proven merge.</param>
/// <param name="MirrorGroupsRechunked">File-row groups re-ingested from their source file on disk.</param>
/// <param name="GroupsUnchanged">Groups whose rows already reproduce at the resolved budget.</param>
/// <param name="RetryableSkipped">Note groups no join proves — untouched, retried on a later pass.</param>
/// <param name="UnprovableSkipped">Note groups that stayed unprovable through the retry bound — untouched,
/// terminal for this budget.</param>
/// <param name="TerminalSkipped">Mirror groups whose source file is gone — nothing re-chunkable exists.</param>
/// <param name="Elapsed">The phase's wall-clock duration, measured from its first read to its stamp
/// write — the number event 448 carries.</param>
public sealed record ChunkRebudgetReport(
    int Budget,
    int NoteGroupsRechunked,
    int MirrorGroupsRechunked,
    int GroupsUnchanged,
    int RetryableSkipped,
    int UnprovableSkipped,
    int TerminalSkipped,
    TimeSpan Elapsed);

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

    /// <summary>Re-chunks every group at the resolved budget; the <c>embedding.chunkBudget</c> stamp is written
    /// at zero retryable skips and cleared otherwise, so it exists only for a bank a run converged.</summary>
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
public sealed partial class ChunkBudgetReconciler(
    IFileTypeMatcher fileTypeMatcher,
    IMarkdownChunker noteChunker,
    IEmbeddingService embeddingService,
    TimeProvider timeProvider,
    Func<IMemoryStore> memoryStore,
    ILogger<ChunkBudgetReconciler> logger) : IChunkBudgetReconciler
{
    /// <summary>How many server starts a permanently-unprovable group may withhold the stamp before it is
    /// left terminal for this budget (config-D F5) — the bound that stops the migration re-opening forever.</summary>
    internal const int MaxRetryAttempts = 3;

    /// <summary>The persisted retry budget, keyed by the resolved budget so a new budget starts fresh.</summary>
    private const string RetryAttemptsKey = "embedding.chunkBudget.retryAttempts";

    /// <inheritdoc />
    public async Task<ChunkBudgetState> CheckAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);

        var budget = await new ChunkPositionScanner(fileTypeMatcher, embeddingService)
            .BudgetAsync(connection, cancellationToken);
        var stamp = await connection.ReadSettingAsync(EmbeddingSettingsKeys.ChunkBudget, cancellationToken);
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

        var startedAt = timeProvider.GetTimestamp();
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
                // A single row needs no merge: when it already fits the resolved budget it IS one
                // current-budget chunk, so it is unchanged — never retryable forever (config-D F5;
                // a body with mixed line endings hashes to no homogeneous variant).
                if (rows.Count == 1 && budget.CountTokens(rows[0].Value) <= budget.MaxTokens)
                {
                    unchanged++;
                }
                else
                {
                    retryable++;
                }

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
                new
                {
                    projectId = group.ProjectId, sourceFile = group.SourceFile, scope = group.Scope,
                    contextLabel = group.ContextLabel, workspaceId = group.WorkspaceId
                }, cancellationToken: cancellationToken))).ToList();
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

            // The same unconditional replace the reingest repair uses (scan-and-replace), now per
            // context (config-D F1/F2): it chunks from disk at the same resolved budget, re-ingests
            // under this group's own scope/context/workspace, and prunes only this bucket — so the
            // same file stored under two contexts keeps both and never silently relabels to project.
            await memoryStore().ReplaceAsync(group.ProjectId, group.SourceFile,
                WatchDigestExecutor.ComputeHash(group.SourceFile, scan.Content), MirrorContext(group), cancellationToken);
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

        // The stamp gate (review F1 mech 4): the stamp is present only for a bank a run converged at
        // this budget — written at zero retryable skips, CLEARED otherwise (a mixed or unproven bank
        // never carries a stale claim), so the drift trigger retries it, once per server start, until
        // the groups converge. Terminal skips do not gate the stamp (nothing re-chunkable exists).
        // The retry bound (config-D F5) bounds that retry: a group still unprovable after
        // MaxRetryAttempts server starts is left terminal for this budget instead of re-opening the
        // migration forever. The counter is persisted in settings, keyed by the resolved budget, and
        // cleared whenever a pass converges; at the bound it stays at the bound, so every later pass
        // at this budget leaves the residual unproven population terminal instead of spending three
        // fresh windows on it.
        var attempts = await ReadRetryAttemptsAsync(connection, budget.MaxTokens, cancellationToken);
        var unprovable = 0;
        if (retryable > 0 && attempts + 1 >= MaxRetryAttempts)
        {
            unprovable = retryable;
            retryable = 0;
            await WriteRetryAttemptsAsync(connection, budget.MaxTokens, MaxRetryAttempts, cancellationToken);
        }
        else if (retryable > 0)
        {
            await WriteRetryAttemptsAsync(connection, budget.MaxTokens, attempts + 1, cancellationToken);
        }
        else
        {
            await DeleteRetryAttemptsAsync(connection, cancellationToken);
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
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.DeleteSetting,
                new { key = EmbeddingSettingsKeys.ChunkBudget }, cancellationToken: cancellationToken));
        }

        var report = new ChunkRebudgetReport(budget.MaxTokens, notesRechunked, mirrorsRechunked, unchanged,
            retryable, unprovable, terminal, timeProvider.GetElapsedTime(startedAt));
        // Event 448 (config-D F4): the report's counts were discarded before, so a partial migration
        // (retryable or unprovable skips) was invisible to an operator; the phase's wall-clock
        // duration (#809) is the other fact only this line can carry.
        Log.RebudgetCompleted(logger, report.Budget, report.NoteGroupsRechunked, report.MirrorGroupsRechunked,
            report.GroupsUnchanged, report.RetryableSkipped, report.UnprovableSkipped, report.TerminalSkipped,
            report.Elapsed);
        return report;
    }

    /// <summary>The context string that maps back to a mirror group's own bucket — so a replace
    /// re-ingests under the scope/context/workspace the rows were stored under, never the project default.</summary>
    private static string MirrorContext(MirrorGroup group) =>
        group.WorkspaceId is not null
            ? ContextNaming.WorkspaceContext(group.WorkspaceId)
            : group.Scope switch
            {
                "shared" => ContextNaming.SharedContext,
                "custom" when string.IsNullOrEmpty(group.ContextLabel) => "",
                "custom" => ContextNaming.LabelContext(group.ProjectId, group.ContextLabel!),
                _ => ContextNaming.ProjectContext(group.ProjectId)
            };

    private static async Task<int> ReadRetryAttemptsAsync(SqliteConnection connection, int budget,
        CancellationToken cancellationToken)
    {
        var stored = await connection.ReadSettingAsync(RetryAttemptsKey, cancellationToken);
        if (stored is null)
        {
            return 0;
        }

        var separator = stored.IndexOf(':');
        return separator > 0
               && int.TryParse(stored[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamped)
               && stamped == budget
               && int.TryParse(stored[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                   out var attempts)
            ? attempts
            : 0;
    }

    private static Task WriteRetryAttemptsAsync(SqliteConnection connection, int budget, int attempts,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
            new
            {
                key = RetryAttemptsKey,
                value = $"{budget.ToString(CultureInfo.InvariantCulture)}:{attempts.ToString(CultureInfo.InvariantCulture)}"
            }, cancellationToken: cancellationToken));

    private static Task DeleteRetryAttemptsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(MemorySql.DeleteSetting,
            new { key = RetryAttemptsKey }, cancellationToken: cancellationToken));

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
        await connection.InWriteTransactionAsync(async () =>
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
                await connection.ExecuteAsync(new CommandDefinition(MemorySql.InsertRebudgetEntry,
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
                        ttlDays = template.TtlDays,
                        // Plain notes keep WriteChunks' sentinel; the phase-end repair positions citing notes.
                        chunkIndex = -1,
                        totalChunks = 0
                    }, cancellationToken: cancellationToken));
            }
        }, cancellationToken);
    }

    private sealed record NoteGroup(string? Scope, string? ProjectId, string? ContextLabel, string? WorkspaceId,
        string? Path);

    private sealed record GroupRow(long Id, string Hash, string Value, string? SourceFile, string? Section,
        string? Scope, string? ProjectId, string? ContextLabel, string? WorkspaceId, string? AgentId,
        long CreatedAt, long? SourceId, long ChunkIndex, long? TtlDays);

    private sealed record MirrorGroup(string? Scope, string ProjectId, string? ContextLabel, string? WorkspaceId,
        string SourceFile);

    private sealed record MirrorRow(long Id, string Hash);

    /// <summary>Event 448 (config-D F4): one line per phase run with the report's counts and its
    /// wall-clock duration, so a partial migration is visible in production — re-chunked, unchanged,
    /// retryable, unprovable, terminal, and how long the phase took.</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 448, Level = LogLevel.Information,
            Message = "Chunk-budget rebudget at {Budget} tokens: {NoteGroupsRechunked} note group(s) and {MirrorGroupsRechunked} mirror group(s) re-chunked, {GroupsUnchanged} unchanged, {RetryableSkipped} retryable, {UnprovableSkipped} unprovable, {TerminalSkipped} terminal in {Elapsed}")]
        public static partial void RebudgetCompleted(ILogger logger, int budget, int noteGroupsRechunked,
            int mirrorGroupsRechunked, int groupsUnchanged, int retryableSkipped, int unprovableSkipped,
            int terminalSkipped, TimeSpan elapsed);
    }
}

/// <summary>
///     The do-nothing chunk-budget rebudget pass: every construction of <see cref="EntryEmbedder" /> (or
///     <see cref="IChunkBudgetReconciler" /> consumer) that does not exercise config-D's re-chunk phase gets
///     this one, so the phase and its drift trigger stay inert wherever they predate it.
/// </summary>
public sealed class NoOpChunkBudgetReconciler : IChunkBudgetReconciler
{
    /// <summary>The shared instance — stateless, so one serves every inert construction.</summary>
    public static readonly NoOpChunkBudgetReconciler Instance = new();

    /// <inheritdoc />
    public Task<ChunkBudgetState> CheckAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChunkBudgetState(true, false, 0));

    /// <inheritdoc />
    public Task<ChunkRebudgetReport> RunAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChunkRebudgetReport(0, 0, 0, 0, 0, 0, 0, TimeSpan.Zero));
}
