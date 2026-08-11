using AiRaccoon.Core.Memory;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     IMemorySourceStore over the memory_source table: resolves or creates source rows
///     by their natural key (source_type, source_locator, section).
/// </summary>
public sealed class SqliteMemorySourceStore(SqliteConnectionFactory factory) : IMemorySourceStore
{
    public async Task<MemorySource> ResolveOrCreateAsync(
        SourceType sourceType,
        string sourceLocator,
        string? section,
        string? headingPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceLocator);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await ResolveOrCreateOnConnectionAsync(connection, sourceType, sourceLocator, section, headingPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves or creates a source row using an already-open connection. Use this overload
    ///     when the caller already holds a write lock (e.g. inside a BEGIN IMMEDIATE transaction).
    /// </summary>
    public async Task<MemorySource> ResolveOrCreateOnConnectionAsync(
        SqliteConnection connection,
        SourceType sourceType,
        string sourceLocator,
        string? section,
        string? headingPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceLocator);

        var sourceTypeText = sourceType switch
        {
            SourceType.File => "file",
            SourceType.Transcript => "transcript",
            SourceType.Manual => "manual",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Invalid SourceType")
        };

        await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT OR IGNORE INTO memory_source (source_type, source_locator, section, heading_path)
                    VALUES (@sourceType, @sourceLocator, @section, @headingPath);
                    """,
                    new { sourceType = sourceTypeText, sourceLocator, section, headingPath },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var row = await connection.QueryFirstOrDefaultAsync<SourceRow>(
                new CommandDefinition(
                    """
                    SELECT id AS Id, source_type AS SourceType, source_locator AS SourceLocator,
                           section AS Section, heading_path AS HeadingPath
                    FROM memory_source
                    WHERE source_type = @sourceType
                      AND source_locator = @sourceLocator
                      AND COALESCE(section, '') IS COALESCE(@section, '')
                    LIMIT 1;
                    """,
                    new { sourceType = sourceTypeText, sourceLocator, section },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (row is null)
        {
            throw new InvalidOperationException(
                $"ResolveOrCreateAsync: failed to find or create source for ({sourceTypeText}, '{sourceLocator}', '{section}')");
        }

        var resolvedType = row.SourceType switch
        {
            "file" => SourceType.File,
            "transcript" => SourceType.Transcript,
            "manual" => SourceType.Manual,
            _ => throw new InvalidOperationException($"Unknown source_type: {row.SourceType}")
        };

        return new MemorySource
        {
            Id = row.Id,
            SourceType = resolvedType,
            SourceLocator = row.SourceLocator,
            Section = row.Section,
            HeadingPath = row.HeadingPath
        };
    }

    private sealed record SourceRow(long Id, string SourceType, string SourceLocator, string? Section, string? HeadingPath);
}
