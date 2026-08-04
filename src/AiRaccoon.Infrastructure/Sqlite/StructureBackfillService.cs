using System.Globalization;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

public sealed record StructureBackfillResult(
    int RowsProcessed,
    int ChunksWithHeadings,
    int UniqueHeadingPaths,
    int VectorsWritten);

/// <summary>
///     Structure backfill (plan C Wave 6): derives heading paths from chunk content, embeds
///     them, writes heading_path + structure_embedding + vec_structure. Idempotent — never
///     touches chunk content or hashes.
/// </summary>
public sealed class StructureBackfillService(EmbeddingService embeddings)
{
    private const int EmbedBatchSize = 32;

    public async Task<StructureBackfillResult> RunAsync(string dbPath,
        double alpha = StructureFusion.DefaultAlpha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentOutOfRangeException.ThrowIfNegative(alpha);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(alpha, 1.0);

        var ensured = await BundledModel.EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!ensured.AllPresent)
        {
            throw new InvalidOperationException(
                $"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");
        }

        await using var connection = await OpenAsync(dbPath, cancellationToken).ConfigureAwait(false);

        var rows = (await connection.QueryAsync<BackfillRow>(
                    new CommandDefinition(
                        "SELECT id AS Id, value AS Value FROM entries ORDER BY id",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false))
            .ToList();

        var headingPaths = rows.ToDictionary(row => row.Id, row => HeadingPathParser.Parse(row.Value));
        var uniquePaths = headingPaths.Values
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var vectors = await EmbedAsync(uniquePaths, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var written = 0;
        foreach (var row in rows)
        {
            var headingPath = headingPaths[row.Id];
            if (headingPath.Length == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE entries SET heading_path = NULL, structure_embedding = NULL WHERE id = @id",
                        new { id = row.Id }, transaction, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM vec_structure WHERE rowid = @id",
                        new { id = row.Id }, transaction, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                continue;
            }

            var blob = EmbeddingBlob.ToBytes(vectors[headingPath]);
            await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE entries SET heading_path = @path, structure_embedding = @embedding WHERE id = @id",
                    new { path = headingPath, embedding = blob, id = row.Id }, transaction,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            // DELETE-then-INSERT mirrors the vec_entries trigger pattern; re-running replaces.
            await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM vec_structure WHERE rowid = @id",
                    new { id = row.Id }, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO vec_structure(rowid, embedding) VALUES (@id, @embedding)",
                    new { id = row.Id, embedding = blob }, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            written++;
        }

        // The bank carries its fusion constant so search behaves identically wherever the db goes.
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { key = StructureFusion.AlphaSettingKey, value = alpha.ToString(CultureInfo.InvariantCulture) },
                transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new StructureBackfillResult(
            rows.Count,
            headingPaths.Values.Count(path => path.Length > 0),
            uniquePaths.Count,
            written);
    }

    private async Task<Dictionary<string, float[]>> EmbedAsync(IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, float[]>(paths.Count);
        if (paths.Count == 0)
        {
            return result;
        }

        var generator = embeddings.CreateGenerator(new EmbeddingSettings("local", null, null, null));
        for (var offset = 0; offset < paths.Count; offset += EmbedBatchSize)
        {
            var batch = paths.Skip(offset).Take(EmbedBatchSize).ToList();
            var generated = await generator.GenerateAsync(batch, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                result[batch[i]] = generated[i].Vector.ToArray();
            }
        }

        return result;
    }

    private static async Task<SqliteConnection> OpenAsync(string dbPath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL";
        await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private sealed class BackfillRow
    {
        public long Id { get; set; }

        public string Value { get; set; } = "";
    }
}
