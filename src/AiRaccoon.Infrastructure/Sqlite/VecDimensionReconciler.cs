using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Brings the vec0 tables to the dimension the active engine embeds at (plan D3).</summary>
public interface IVecDimensionReconciler
{
    /// <summary>Recreates `vec_entries`/`vec_structure` at <paramref name="targetDimension" /> when
    /// either is missing or declared at another dimension. True when the bank changed.</summary>
    Task<bool> ReconcileAsync(SqliteConnection connection, int targetDimension, CancellationToken cancellationToken);
}

/// <summary>
///     Create-if-missing-or-mismatch in ONE `BEGIN IMMEDIATE` transaction, with no repopulate
///     (plan D3). Three things this deliberately does not do, each of which wedges the bank:
///     it never calls <c>RebuildVecTableAsync</c>, whose repopulate reads the `entries` blob columns
///     that still hold OLD-dimension vectors after `MarkAllEmbeddedPending`; it never infers presence
///     from a dimension read, because that helper answers 384 for a missing table; and it never
///     relies on the `Ddl` block to recreate anything, because that block is digest-gated (ADR-0075)
///     and a runtime DROP does not change the digest.
/// </summary>
public sealed partial class VecDimensionReconciler : IVecDimensionReconciler
{
    private static readonly string[] VecTables = ["vec_entries", "vec_structure"];

    public async Task<bool> ReconcileAsync(SqliteConnection connection, int targetDimension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetDimension, 0);

        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            foreach (var table in VecTables)
            {
                if (await NeedsRecreateAsync(connection, transaction, table, targetDimension, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await RecreateAsync(connection, transaction, table, targetDimension, cancellationToken)
                        .ConfigureAwait(false);
                    changed = true;
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return changed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<bool> NeedsRecreateAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, int targetDimension, CancellationToken cancellationToken)
    {
        var sql = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Presence is read explicitly: a missing table is not a table that already matches.
        if (sql is null)
        {
            return true;
        }

        var match = DimensionPattern().Match(sql);
        return !match.Success
               || int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) != targetDimension;
    }

    private static async Task RecreateAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, int targetDimension, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"DROP TABLE IF EXISTS {table}", transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The six vec triggers survive the DROP and bind to the recreated table by name; the drain's
        // MarkEmbedded refills both tables through them, so there is nothing to repopulate here.
        await connection.ExecuteAsync(new CommandDefinition(
            $"CREATE VIRTUAL TABLE {table} USING vec0(ctx TEXT, embedding float[{targetDimension}] distance_metric=cosine)",
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    [GeneratedRegex(@"float\[(\d+)\]")]
    private static partial Regex DimensionPattern();
}
