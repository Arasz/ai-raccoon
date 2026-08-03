using System.Net.Http.Json;
using System.Text.Json;
using AiRaccoon.Benchmarks.Corpus;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
/// An embedding backend served by LM Studio's OpenAI-compatible /v1/embeddings endpoint
/// (http://localhost:1234 by default). Retrieval is brute-force cosine similarity over the
/// returned vectors — the same ranking shape the local path produces, so quality metrics
/// are comparable across models. The sqlite-memory extension cannot host LM Studio models
/// directly (its remote engine hardcodes the vectors.space URL), hence this standalone path.
/// </summary>
public sealed class LmStudioEmbedder : IEmbedder
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly IReadOnlyList<CorpusDocument> _documents = BenchmarkCorpus.Documents;
    private IReadOnlyList<float[]> _documentVectors = [];

    public LmStudioEmbedder(string model, string? baseUrl = null)
    {
        _model = model;
        _baseUrl = baseUrl ?? Environment.GetEnvironmentVariable("LMSTUDIO_BASE_URL") ?? "http://localhost:1234";
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public string Name => $"lmstudio:{_model}";

    public int Dimensions { get; private set; }

    public async Task IndexAsync(IReadOnlyList<CorpusDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var texts = documents.Select(d => d.Text).ToArray();
        var vectors = await EmbedAsync(texts, cancellationToken);
        Dimensions = vectors[0].Length;
        _documentVectors = vectors;
    }

    public Task<IReadOnlyList<RetrievalHit>> SearchAsync(string query, int topK,
        CancellationToken cancellationToken = default)
    {
        var queryVector = EmbedAsync([query], cancellationToken).GetAwaiter().GetResult()[0];

        var scored = new List<(string Id, double Score)>(_documents.Count);
        for (var i = 0; i < _documents.Count; i++)
        {
            scored.Add((_documents[i].Id, Cosine(queryVector, _documentVectors[i])));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return Task.FromResult<IReadOnlyList<RetrievalHit>>(
            scored.Take(topK).Select(s => new RetrievalHit(s.Id, s.Score)).ToList());
    }

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(
            $"{_baseUrl}/v1/embeddings",
            new { model = _model, input = text },
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(cancellationToken);
        return body?.Data?[0]?.Embedding
            ?? throw new InvalidOperationException($"LM Studio returned no embedding for model {_model}.");
    }

    private async Task<float[][]> EmbedAsync(string[] texts, CancellationToken cancellationToken)
    {
        var response = await _http.PostAsJsonAsync(
            $"{_baseUrl}/v1/embeddings",
            new { model = _model, input = texts },
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(cancellationToken);
        return body?.Data
                   ?.OrderBy(d => d.Index)
                   .Select(d => d.Embedding)
                   .ToArray()
            ?? throw new InvalidOperationException($"LM Studio returned no embeddings for model {_model}.");
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

    private sealed class EmbeddingsResponse
    {
        public string? Model { get; set; }

        public List<EmbeddingData>? Data { get; set; }
    }

    private sealed class EmbeddingData
    {
        public int Index { get; set; }

        public float[] Embedding { get; set; } = [];
    }
}
