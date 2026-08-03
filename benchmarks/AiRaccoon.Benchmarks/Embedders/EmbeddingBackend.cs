using AiRaccoon.Benchmarks.Corpus;
using Microsoft.Extensions.AI;
using System.Numerics.Tensors;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>A ranked retrieval hit for one query.</summary>
public sealed record RetrievalHit(string DocumentId, double Score);

/// <summary>
///     Retrieval backend over an official Microsoft.Extensions.AI IEmbeddingGenerator. Indexing
///     embeds the corpus once; search embeds the query and ranks documents by cosine similarity.
///     All backends share this ranking shape, so quality metrics are comparable across models.
/// </summary>
public abstract class EmbeddingBackend(IEmbeddingGenerator<string, Embedding<float>> generator) : IEmbedder, IDisposable
{
    private readonly IReadOnlyList<CorpusDocument> _documents = BenchmarkCorpus.Documents;
    private IReadOnlyList<float[]> _documentVectors = [];

    protected abstract string BackendName { get; }

    public virtual void Dispose() => generator.Dispose();

    public string Name => BackendName;

    public int Dimensions { get; private set; }

    public async Task IndexAsync(IReadOnlyList<CorpusDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var texts = documents.Select(d => d.Text).ToArray();
        var results = await generator.GenerateAsync(texts, null, cancellationToken);
        _documentVectors = results.Select(e => e.Vector.ToArray()).ToArray();
        Dimensions = _documentVectors[0].Length;
    }

    public async Task<IReadOnlyList<RetrievalHit>> SearchAsync(string query, int topK,
        CancellationToken cancellationToken = default)
    {
        var results = await generator.GenerateAsync([query], null, cancellationToken);
        var queryVector = results[0].Vector.ToArray();

        var scored = new List<(string Id, double Score)>(_documents.Count);
        for (var i = 0; i < _documents.Count; i++)
        {
            scored.Add((_documents[i].Id, Cosine(queryVector, _documentVectors[i])));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(topK).Select(s => new RetrievalHit(s.Id, s.Score)).ToList();
    }

    private static double Cosine(float[] a, float[] b) =>
        TensorPrimitives.CosineSimilarity(a, b);
}
