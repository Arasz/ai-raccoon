using System.Globalization;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <inheritdoc cref="ICodeEmbedder" />
public sealed partial class CodeEmbedder(
    IEmbeddingService embeddings,
    ILogger<CodeEmbedder> logger,
    IVecDimensionReconciler vecDimensions)
    : ICodeEmbedder
{
    /// <summary>Generator-call sub-batch size (§3.3 D-E9: "batches of 32"), mirroring
    /// EntryEmbedder.EmbedAsync (S8) — a smaller generator call also shrinks S1's stale-engine race
    /// window and S2's poison-row blast radius, so a caller's own drain-run limit (CodeReindexJob's
    /// RowsPerRun) can safely exceed this without either risk growing with it.</summary>
    internal const int BatchSize = 32;

    public async Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return QueryVector.Empty;
        }

        var settings = SettingsFor(codeModel);
        try
        {
            var trimmed = embeddings.TrimQueryToWindow(settings, query);
            var generator = embeddings.CreateGenerator(settings);
            var result = await generator.GenerateAsync([trimmed], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new QueryVector(EmbeddingBlob.ToBytes(result[0].Vector))
            {
                Trimmed = !string.Equals(trimmed, query, StringComparison.Ordinal)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodeEngineUnloadableException(codeModel, ex);
        }
    }

    public async Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
        CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return 0;
        }

        // S1: captured once, before generation begins — the same window a concurrent activation
        // could land in. MarkCodeEmbedded's own WHERE guard re-checks this value at UPDATE time, so
        // a row whose engine moved on between here and the per-row write stays pending instead of
        // being marked embedded under a previous engine's vector.
        var engine = await ReadCodeEngineAsync(connection, cancellationToken).ConfigureAwait(false);

        var rows = (await connection.QueryAsync<EmbedRow>(new CommandDefinition(
                MemorySql.SelectAllPendingCodeForEmbed, new { limit }, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        var settings = SettingsFor(codeModel);
        IEmbeddingGenerator<string, Embedding<float>> generator;
        try
        {
            generator = embeddings.CreateGenerator(settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodeEngineUnloadableException(codeModel, ex);
        }

        // S8: sub-batched at BatchSize, mirroring EntryEmbedder.EmbedAsync — RowsPerRun (a drain
        // run's own limit) can be a multiple of this without growing S1's race window or S2's
        // poison-row blast radius past one BatchSize-sized generator call.
        var embedded = 0;
        for (var offset = 0; offset < rows.Count; offset += BatchSize)
        {
            var slice = rows.Skip(offset).Take(BatchSize).ToList();
            embedded += await EmbedSliceAsync(connection, generator, slice, engine, cancellationToken)
                .ConfigureAwait(false);
        }

        return embedded;
    }

    /// <summary>
    ///     S2: one row's content can break the WHOLE generator call. Falling back to embedding the
    ///     slice one row at a time lets the healthy rows still make progress instead of every row
    ///     — including the healthy ones — staying pending for the maintenance loop's 15s on-demand
    ///     poll to retry identically forever.
    /// </summary>
    private async Task<int> EmbedSliceAsync(SqliteConnection connection,
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<EmbedRow> slice, string? engine,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await generator.GenerateAsync(slice.Select(row => row.Value),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var embedded = 0;
            for (var i = 0; i < slice.Count; i++)
            {
                embedded += await MarkEmbeddedAsync(connection, slice[i].Id, result[i].Vector, engine,
                    cancellationToken).ConfigureAwait(false);
            }

            return embedded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recoverable on its own — the per-row retry below reports whatever actually fails —
            // but a batch call that only fails in a batch is invisible without this line.
            Log.CodeEmbedBatchFellBackToOneRowAtATime(logger, slice.Count, ex);
            return await EmbedRowByRowAsync(connection, generator, slice, engine, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     S2 fallback: one generator call per row. A row that still fails on its own gets its
    ///     attempt count bumped and stays pending — CodeCorpusSchema.MaxEmbedAttempts eventually
    ///     excludes it from future selection, bounding a poison row's cost instead of retrying it
    ///     forever.
    /// </summary>
    private async Task<int> EmbedRowByRowAsync(SqliteConnection connection,
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<EmbedRow> slice, string? engine,
        CancellationToken cancellationToken)
    {
        var embedded = 0;
        foreach (var row in slice)
        {
            try
            {
                var result = await generator.GenerateAsync([row.Value], cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                embedded += await MarkEmbeddedAsync(connection, row.Id, result[0].Vector, engine, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var attempts = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                        MemorySql.IncrementCodeEmbedAttempts, new { id = row.Id }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                Log.CodeRowEmbedAttemptFailed(logger, row.Id, row.Path, attempts,
                    CodeCorpusSchema.MaxEmbedAttempts, ex);
                if (attempts >= CodeCorpusSchema.MaxEmbedAttempts)
                {
                    Log.CodeRowAbandonedAfterMaxEmbedAttempts(logger, row.Id, row.Path, row.SourceFile, attempts);
                }
            }
        }

        return embedded;
    }

    private static async Task<int> MarkEmbeddedAsync(SqliteConnection connection, long id,
        ReadOnlyMemory<float> vector, string? engine, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.MarkCodeEmbedded,
                new { id, embedding = EmbeddingBlob.ToBytes(vector), engine }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    public async Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return false;
        }

        var currentFingerprint = embeddings.EngineFingerprint("local", codeModel, null);
        var storedFingerprint = await ReadCodeEngineAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.Equals(currentFingerprint, storedFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        var dimensions = embeddings.ResolveDimensions(SettingsFor(codeModel));
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeEngine, value = currentFingerprint }, transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key = EmbeddingSettingsKeys.CodeDimensions, value = dimensions.ToString(CultureInfo.InvariantCulture) },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await vecDimensions.ReconcileCodeAsync(connection, transaction, dimensions, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.MarkAllCodeEmbeddedPending,
                transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return true;
    }

    public async Task<bool> ReconcileVecCodeDimensionsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return false;
        }

        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            MemorySql.SelectSetting, new { key = EmbeddingSettingsKeys.CodeDimensions },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var target = int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : CodeCorpusSchema.EmbeddingDimensions;

        return await vecDimensions.ReconcileCodeAsync(connection, target, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var codeModel = await ReadCodeModelAsync(connection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(codeModel))
        {
            return false; // no engine configured: a pending row here is legitimately unembeddable
        }

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                MemorySql.HasPendingCodeEmbed, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>The code corpus always activates local + a manifest directory (§3.3) — no openai/remote code engine in v1.</summary>
    private static EmbeddingSettings SettingsFor(string codeModel) => new("local", codeModel, null, null);

    private static async Task<string?> ReadCodeModelAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(MemorySql.SelectSetting,
                new { key = EmbeddingSettingsKeys.CodeModel }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task<string?> ReadCodeEngineAsync(SqliteConnection connection,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(MemorySql.SelectSetting,
                new { key = EmbeddingSettingsKeys.CodeEngine }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    ///     #466: a code row that cannot embed used to reach the attempt ceiling and drop out of the
    ///     drain's selection with nothing written anywhere — a column value was the only evidence.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 421, Level = LogLevel.Debug,
            Message = "A batch of {Rows} code rows failed to embed as one call; retrying them one at a time")]
        public static partial void CodeEmbedBatchFellBackToOneRowAtATime(ILogger logger, int rows, Exception exception);

        [LoggerMessage(EventId = 422, Level = LogLevel.Warning,
            Message = "Code chunk {Path} (row {RowId}) failed to embed on attempt {Attempts} of {MaxAttempts}; "
                      + "it stays pending and the drain will retry it")]
        public static partial void CodeRowEmbedAttemptFailed(ILogger logger, long rowId, string path, int attempts,
            int maxAttempts, Exception exception);

        [LoggerMessage(EventId = 423, Level = LogLevel.Error,
            Message = "Giving up on code chunk {Path} from {SourceFile} (row {RowId}) after {Attempts} failed embed "
                      + "attempts: it will never be selected for embedding again and code search cannot match it on "
                      + "meaning. Fix the engine or re-index {SourceFile} once the cause is gone.")]
        public static partial void CodeRowAbandonedAfterMaxEmbedAttempts(ILogger logger, long rowId, string path,
            string sourceFile, int attempts);
    }

    private sealed record EmbedRow
    {
        public long Id { get; init; }

        public string Value { get; init; } = "";

        /// <summary>The chunk's own path (file plus symbol) — what a reader needs to find the row that failed.</summary>
        public string Path { get; init; } = "";

        /// <summary>The file the chunk was extracted from.</summary>
        public string SourceFile { get; init; } = "";
    }
}
