using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Brings the vec0 tables to the dimension the active engine embeds at (plan D3).</summary>
public interface IVecDimensionReconciler
{
    /// <summary>Recreates <paramref name="tables" /> at <paramref name="targetDimension" /> when
    /// any is missing or declared at another dimension. When <paramref name="transaction" /> is
    /// passed the caller owns it (the reconciler never begins, commits or rolls back); otherwise
    /// the reconciler begins and commits its own. True when any table changed.</summary>
    Task<bool> ReconcileAsync(SqliteConnection connection, SqliteTransaction? transaction,
        int targetDimension, IReadOnlyCollection<string> tables, CancellationToken cancellationToken);
}

/// <summary>
///     Create-if-missing-or-mismatch in ONE transaction, with no repopulate
///     (plan D3). Three things this deliberately does not do, each of which wedges the bank:
///     it never calls <c>RebuildVecTableAsync</c>, whose repopulate reads the `entries` blob columns
///     that still hold OLD-dimension vectors after `MarkAllEmbeddedPending`; it never infers presence
///     from a dimension read, because that helper answers 384 for a missing table; and it never
///     relies on the `Ddl` block to recreate anything, because that block is digest-gated (ADR-0075)
///     and a runtime DROP does not change the digest.
/// </summary>
public sealed partial class VecDimensionReconciler : IVecDimensionReconciler
{
    /// <summary>The memory bank's two vec0 tables.</summary>
    public static readonly IReadOnlyCollection<string> MemoryVecTables = ["vec_entries", "vec_structure"];

    /// <summary>The code corpus's vec0 table.</summary>
    public static readonly IReadOnlyCollection<string> CodeVecTables = ["vec_code"];

    public async Task<bool> ReconcileAsync(SqliteConnection connection, SqliteTransaction? transaction,
        int targetDimension, IReadOnlyCollection<string> tables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetDimension, 0);

        var ownsTransaction = transaction is null;
        var tx = transaction ?? (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var changed = false;
        try
        {
            foreach (var table in tables)
            {
                if (await NeedsRecreateAsync(connection, tx, table, targetDimension, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await RecreateAsync(connection, tx, table, targetDimension, cancellationToken)
                        .ConfigureAwait(false);
                    changed = true;
                }
            }

            if (ownsTransaction)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return changed;
        }
        catch
        {
            if (ownsTransaction)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

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
