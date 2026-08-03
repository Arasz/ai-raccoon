using AiRaccoon.Benchmarks.Corpus;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
///     An embedding backend plus its retrieval path. All backends (local GGUF via LLamaSharp,
///     LM Studio via the official OpenAI SDK) wrap a Microsoft.Extensions.AI IEmbeddingGenerator
///     and share the same cosine ranking, so quality metrics are comparable across models.
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
