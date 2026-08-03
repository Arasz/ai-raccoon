using AiRaccoon.Benchmarks.Corpus;
using Microsoft.Extensions.AI;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>A ranked retrieval hit for one query.</summary>
public sealed record RetrievalHit(string DocumentId, double Score);

/// <summary>
/// Retrieval backend over an official Microsoft.Extensions.AI IEmbeddingGenerator. Indexing
/// embeds the corpus once; search embeds the query and ranks documents by cosine similarity.
/// All backends share this ranking shape, so quality metrics are comparable across models.
/// </summary>
public abstract class EmbeddingBackend : IEmbedder, IDisposable
{
    private readonly IReadOnlyList<CorpusDocument> _documents = BenchmarkCorpus.Documents;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private IReadOnlyList<float[]> _documentVectors = [];

    protected EmbeddingBackend(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _generator = generator;
    }

    protected abstract string BackendName { get; }

    public string Name => BackendName;

    public int Dimensions { get; private set; }

    public async Task IndexAsync(IReadOnlyList<CorpusDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var texts = documents.Select(d => d.Text).ToArray();
        var results = await _generator.GenerateAsync(texts, options: null, cancellationToken);
        _documentVectors = results.Select(e => e.Vector.ToArray()).ToArray();
        Dimensions = _documentVectors[0].Length;
    }

    public async Task<IReadOnlyList<RetrievalHit>> SearchAsync(string query, int topK,
        CancellationToken cancellationToken = default)
    {
        var results = await _generator.GenerateAsync([query], options: null, cancellationToken);
        var queryVector = results[0].Vector.ToArray();

        var scored = new List<(string Id, double Score)>(_documents.Count);
        for (var i = 0; i < _documents.Count; i++)
        {
            scored.Add((_documents[i].Id, Cosine(queryVector, _documentVectors[i])));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Take(topK).Select(s => new RetrievalHit(s.Id, s.Score)).ToList();
    }

    public virtual void Dispose()
    {
        _generator.Dispose();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
