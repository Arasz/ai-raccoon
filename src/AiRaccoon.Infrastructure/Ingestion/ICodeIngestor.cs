using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>Ingests one file into the code corpus (`code_entries`), self-filtering by extension.</summary>
public interface ICodeIngestor
{
    Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken);
}
