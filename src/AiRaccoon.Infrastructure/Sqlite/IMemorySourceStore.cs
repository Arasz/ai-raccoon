using AiRaccoon.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Resolves or creates a <see cref="MemorySource" /> row by its natural key
///     (sourceType, locator, section). Idempotent: same key → same Id.
/// </summary>
public interface IMemorySourceStore
{
    Task<MemorySource> ResolveOrCreateAsync(
        SourceType sourceType,
        string sourceLocator,
        string? section,
        string? headingPath,
        CancellationToken cancellationToken = default);

    Task<MemorySource> ResolveOrCreateOnConnectionAsync(
        SqliteConnection connection,
        SourceType sourceType,
        string sourceLocator,
        string? section,
        string? headingPath,
        CancellationToken cancellationToken = default);
}
