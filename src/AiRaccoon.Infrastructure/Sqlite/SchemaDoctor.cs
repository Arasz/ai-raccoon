using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Read-only schema-shape verification: `CREATE ... IF NOT EXISTS` silently no-ops
///     against an object that already exists with a different shape, so <see cref="MemorySchema.EnsureAsync" />
///     proves existence, never shape. Compares <paramref name="bank" /> against the real
///     <c>Ddl</c> applied to a throwaway in-memory bank — never a hand-maintained second copy of
///     the schema — and reports; it never writes to <paramref name="bank" />.
/// </summary>
internal static class SchemaDoctor
{
    public static async Task<SchemaDoctorReport> DiagnoseAsync(SqliteConnection bank, CancellationToken cancellationToken)
    {
        var storedVersion = await bank.ExecuteScalarAsync<long>(
            new CommandDefinition("PRAGMA user_version", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var storedDigest = await bank.ExecuteScalarAsync<int>(
            new CommandDefinition("PRAGMA application_id", cancellationToken: cancellationToken)).ConfigureAwait(false);

        // A bank stamped by a newer build than this one has a shape this binary cannot know —
        // diffing it against our own current shape would misreport version skew as corruption.
        if (storedVersion > MemorySchema.CurrentVersion)
        {
            return new SchemaDoctorReport(SchemaDoctorStatus.VersionAheadOfBinary, storedVersion,
                MemorySchema.CurrentVersion, storedDigest, MemorySchema.SchemaDigest, []);
        }

        await using var expected = await BuildExpectedSchemaConnectionAsync(cancellationToken).ConfigureAwait(false);
        var findings = await CompareAsync(expected, bank, cancellationToken).ConfigureAwait(false);
        var status = findings.Count == 0 ? SchemaDoctorStatus.Healthy : SchemaDoctorStatus.ShapeMismatch;
        return new SchemaDoctorReport(status, storedVersion, MemorySchema.CurrentVersion, storedDigest, MemorySchema.SchemaDigest, findings);
    }

    /// <summary>The expected shape: the real <c>Ddl</c> applied to a throwaway in-memory bank via <see cref="MemorySchema.EnsureAsync" />.</summary>
    private static async Task<SqliteConnection> BuildExpectedSchemaConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Ddl declares vec0 virtual tables, so the module has to be loaded first, exactly as
        // SqliteConnectionFactory.InitializeAsync does.
        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<IReadOnlyList<SchemaFinding>> CompareAsync(SqliteConnection expected, SqliteConnection actual,
        CancellationToken cancellationToken)
    {
        var findings = new List<SchemaFinding>();

        var expectedObjects = await ObjectInventoryAsync(expected, cancellationToken).ConfigureAwait(false);
        var actualObjects = await ObjectInventoryAsync(actual, cancellationToken).ConfigureAwait(false);

        foreach (var (type, name) in expectedObjects)
        {
            if (!actualObjects.Contains((type, name)))
            {
                findings.Add(new SchemaFinding(name, $"missing {type}"));
                continue;
            }

            if (type == "table")
            {
                findings.AddRange(await CompareColumnsAsync(expected, actual, name, cancellationToken).ConfigureAwait(false));
            }
        }

        return findings;
    }

    /// <summary>Every non-sqlite_ table/index/trigger — the object inventory both sides are diffed against.</summary>
    private static async Task<HashSet<(string Type, string Name)>> ObjectInventoryAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<ObjectRow>(
            new CommandDefinition(
                "SELECT type, name FROM sqlite_master WHERE type IN ('table','index','trigger') AND name NOT LIKE 'sqlite_%'",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => (r.Type, r.Name))];
    }

    /// <summary>Column-level diff for a table both sides agree exists — the "different shape" #357 is about.</summary>
    private static async Task<IReadOnlyList<SchemaFinding>> CompareColumnsAsync(SqliteConnection expected, SqliteConnection actual,
        string table, CancellationToken cancellationToken)
    {
        var expectedColumns = await ColumnsAsync(expected, table, cancellationToken).ConfigureAwait(false);
        var actualColumns = await ColumnsAsync(actual, table, cancellationToken).ConfigureAwait(false);

        var findings = new List<SchemaFinding>();
        foreach (var (name, column) in expectedColumns)
        {
            if (!actualColumns.TryGetValue(name, out var actualColumn))
            {
                findings.Add(new SchemaFinding(table, $"missing column '{name}' (expected {Describe(column)})"));
                continue;
            }

            if (!string.Equals(column.Type, actualColumn.Type, StringComparison.OrdinalIgnoreCase)
                || column.NotNull != actualColumn.NotNull || column.IsPrimaryKey != actualColumn.IsPrimaryKey)
            {
                findings.Add(new SchemaFinding(table,
                    $"column '{name}' shape mismatch (expected {Describe(column)}, found {Describe(actualColumn)})"));
            }
        }

        foreach (var name in actualColumns.Keys.Except(expectedColumns.Keys, StringComparer.Ordinal))
        {
            findings.Add(new SchemaFinding(table, $"unexpected column '{name}' (found {Describe(actualColumns[name])})"));
        }

        return findings;
    }

    private static string Describe(ColumnShape column) => $"{column.Type}{(column.NotNull ? " NOT NULL" : "")}{(column.IsPrimaryKey ? " PRIMARY KEY" : "")}";

    private static async Task<IReadOnlyDictionary<string, ColumnShape>> ColumnsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken)
    {
        // Table names come only from this bank's own sqlite_master (never user input), matching
        // MemorySchemaVersionTests' ColumnsAsync helper.
        var rows = await connection.QueryAsync<ColumnRow>(
            new CommandDefinition($"SELECT name, type, \"notnull\", pk FROM pragma_table_info('{table}')",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Name, r => new ColumnShape(r.Type, r.NotNull != 0, r.Pk != 0), StringComparer.Ordinal);
    }

    private sealed record ObjectRow(string Type, string Name);

    private sealed record ColumnRow(string Name, string Type, long NotNull, long Pk);

    private readonly record struct ColumnShape(string Type, bool NotNull, bool IsPrimaryKey);
}

internal enum SchemaDoctorStatus
{
    Healthy,
    ShapeMismatch,
    VersionAheadOfBinary
}

/// <summary>One shape defect: <paramref name="ObjectName" /> (a table/index/trigger name) and what is wrong with it.</summary>
internal sealed record SchemaFinding(string ObjectName, string Detail);

internal sealed record SchemaDoctorReport(
    SchemaDoctorStatus Status,
    long StoredVersion,
    int CurrentVersion,
    int StoredDigest,
    int ExpectedDigest,
    IReadOnlyList<SchemaFinding> Findings);
