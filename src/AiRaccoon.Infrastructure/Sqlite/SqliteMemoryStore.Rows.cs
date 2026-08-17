using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Dapper row-mapping DTOs for <see cref="SqliteMemoryStore" /> — plain data shapes with no
///     behavior of their own, split out (WP2, docs/plans/2026-08-15-performance-metrics-implementation.md)
///     to keep the store itself under its measured line ratchet
///     (<see cref="AiRaccoon.Tests.Unit.Storage.SqliteMemoryStoreSizeRatchetTests" />).
/// </summary>
public sealed partial class SqliteMemoryStore
{
    private sealed class EntryRow
    {
        public long Id { get; set; }

        public string Hash { get; set; } = "";

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";

        public string? Scope { get; set; }

        public string ProjectId { get; set; } = "";

        public string? ContextLabel { get; set; }

        public string? WorkspaceId { get; set; }

        public long CreatedAt { get; set; }
    }

    internal sealed class SearchRow
    {
        public string Hash { get; set; } = "";

        public double Ranking { get; set; }

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";

        public string? SourceFile { get; set; }

        public int ChunkIndex { get; set; }

        public int TotalChunks { get; set; }

        /// <summary>entries.id — the FTS5 rowid, used to resolve the deferred snippet by row identity rather than hash.</summary>
        public long Id { get; set; }
    }

    /// <summary>Row shape for <see cref="MemorySql.FtsSnippetsForSurvivors" />.</summary>
    private sealed class SnippetRow
    {
        public string Hash { get; set; } = "";

        public string Snippet { get; set; } = "";
    }

    internal sealed class VectorRow
    {
        public string Hash { get; set; } = "";

        public string Path { get; set; } = "";

        public string Value { get; set; } = "";

        public double Distance { get; set; }

        public string? SourceFile { get; set; }

        public int ChunkIndex { get; set; }

        public int TotalChunks { get; set; }
    }

    private sealed record SourceRow(string Path, string Value, string? SourceFile, string? Section, string? SourceType, string? HeadingPath);

    private sealed record DeleteRecomputeRow(string? Scope, string? ContextLabel, string? WorkspaceId, string? SourceFile, long ChunkIndex);

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
        public double Rating { get; set; }

        public int? TtlDays { get; set; }
    }
}
