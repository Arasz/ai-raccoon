using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     Dapper row-mapping DTOs for <see cref="Memory.SqliteMemoryStore" /> — plain data shapes with no
///     behavior of their own, split out (WP2, docs/plans/2026-08-15-performance-metrics-implementation.md)
///     to keep the store itself under its measured line ratchet
///     (<see cref="AiRaccoon.Tests.Unit.Storage.SqliteMemoryStoreSizeRatchetTests" />).
/// </summary>
public sealed partial class SqliteMemoryStore
{
    public sealed class EntryRow
    {
        public long Id { get; init; }

        public string Hash { get; init; } = "";

        public string Path { get; init; } = "";

        public string Value { get; init; } = "";

        public string? Scope { get; init; }

        public string ProjectId { get; init; } = "";

        public string? ContextLabel { get; init; }

        public string? WorkspaceId { get; init; }

        public long CreatedAt { get; init; }

        public string? EntryKind { get; init; }
    }

    internal sealed class SearchRow
    {
        public string Hash { get; init; } = "";

        public double Ranking { get; init; }

        public string Path { get; init; } = "";

        public string Value { get; init; } = "";

        public string? SourceFile { get; init; }

        public int ChunkIndex { get; init; }

        public int TotalChunks { get; init; }

        /// <summary>entries.id — the FTS5 rowid, used to resolve the deferred snippet by row identity rather than hash.</summary>
        public long Id { get; init; }

        public string? EntryKind { get; init; }
    }

    /// <summary>Row shape for <see cref="MemorySql.FtsSnippetsForSurvivors" />.</summary>
    private sealed class SnippetRow
    {
        public string Hash { get; init; } = "";

        public string Snippet { get; init; } = "";
    }

    internal sealed class VectorRow
    {
        public string Hash { get; init; } = "";

        public string Path { get; init; } = "";

        public string Value { get; init; } = "";

        public double Distance { get; init; }

        public string? SourceFile { get; init; }

        public int ChunkIndex { get; init; }

        public int TotalChunks { get; init; }

        public string? EntryKind { get; init; }
    }

    /// <summary>A vector row carrying its alpha-fused rank score and, when available, its own raw content cosine.</summary>
    internal readonly record struct RankedVectorRow(VectorRow Row, double Score, double? ContentCosine);

    private sealed record SourceRow(string Path, string Value, string? SourceFile, string? Section, string? SourceType, string? HeadingPath);

    internal sealed record DeleteRecomputeRow(string? Scope, string? ContextLabel, string? WorkspaceId, string? SourceFile, long ChunkIndex);

    private sealed record SharedRow(string Path, string Value);

    private sealed record ExtractionRow(
        string Hash,
        string Path,
        string Value,
        string? SourceFile,
        string? SourceType,
        double Rating,
        long AccessCount,
        long CreatedAt,
        long? TtlDays)
    {
        public ExtractionCandidateRow ToCandidate() =>
            new(Hash, Path, Value, SourceFile, Rating, (int)AccessCount,
                DateTimeOffset.FromUnixTimeSeconds(CreatedAt), (int?)TtlDays, SourceType);
    }

    private sealed class MetadataRow
    {
        public double Rating { get; init; }

        public int? TtlDays { get; init; }
    }

    /// <summary>A source_file classified into its SourceType and normalized locator.</summary>
    private readonly record struct SourceClassification(SourceType Type, string Locator);
}

internal static class RowExtensions
{
    extension(SqliteMemoryStore.DeleteRecomputeRow row)
    {
        public string ContextStringOf(string projectId) => ContextStringOf(row.Scope, row.ContextLabel, row.WorkspaceId, projectId);
    }

    extension(SqliteMemoryStore.EntryRow row)
    {
        internal MemoryEntry ToEntry() => new(row.Hash, row.Path, ContextStringOf(row), row.Value, row.CreatedAt, EntryKind: row.EntryKind);
    }

    private static string ContextStringOf(SqliteMemoryStore.EntryRow row) => ContextStringOf(row.Scope, row.ContextLabel, row.WorkspaceId, row.ProjectId);

    private static string ContextStringOf(string? scope, string? contextLabel, string? workspaceId, string projectId)
    {
        if (workspaceId is not null)
        {
            return ContextNaming.WorkspaceContext(workspaceId);
        }

        return scope switch
        {
            MemoryScopes.SharedScope => ContextNaming.SharedContext,
            MemoryScopes.ProjectScope => ContextNaming.ProjectContext(projectId),
            MemoryScopes.CustomScope => contextLabel ?? "",
            _ => ""
        };
    }
}
