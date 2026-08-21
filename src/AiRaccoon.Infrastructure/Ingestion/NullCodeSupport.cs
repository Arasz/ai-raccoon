using AiRaccoon.Core.Ingestion;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>Null object for callers that construct <see cref="FileIngestor" /> without code-corpus
/// support (test/legacy positional call sites) — never matches a code extension.</summary>
public sealed class NullCodeFileTypeMatcher : ICodeFileTypeMatcher
{
    public static NullCodeFileTypeMatcher Instance { get; } = new();

    public bool IsCodeFile(string path) => false;
}

/// <summary>Null object for callers that construct <see cref="FileIngestor" /> without code-corpus
/// support — resolves to 0 chunks, ever.</summary>
public sealed class NullCodeIngestor : ICodeIngestor
{
    public static NullCodeIngestor Instance { get; } = new();

    public Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken) => Task.FromResult(0);
}
