using AiRaccoon.Benchmarks.Corpus;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>A ranked retrieval hit for one query.</summary>
public sealed record RetrievalHit(string DocumentId, double Score);

/// <summary>
/// An embedding backend plus its retrieval path. The local GGUF backend runs through the
/// real SqliteMemoryStore (hybrid search); the LM Studio backend embeds via the REST API and
/// retrieves with brute-force cosine similarity. Both consume the same corpus and queries so
/// the quality metrics are comparable.
/// </summary>
public interface IEmbedder
{
    string Name { get; }

    int Dimensions { get; }

    /// <summary>Indexes the corpus so queries can be run against it.</summary>
    Task IndexAsync(IReadOnlyList<CorpusDocument> documents, CancellationToken cancellationToken = default);

    /// <summary>Returns the top-k documents for a query, best first.</summary>
    Task<IReadOnlyList<RetrievalHit>> SearchAsync(string query, int topK, CancellationToken cancellationToken = default);
}
