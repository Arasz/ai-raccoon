using System.Text.Json;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     TEMPORARY DIAGNOSTIC — not a gate. Measures how far the sweep's AdrNdcg5 moves under
///     float-magnitude perturbation of the query embedding, and whether the ONNX session's
///     intra-op thread count changes the embedding at all.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class NdcgSensitivityProbe : IDisposable
{
    private const string ProjectId = "job-search-ai-assistant";
    private const int SearchLimit = 10;
    private const int RankCutoff = 5;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] GateQueryIds =
        ["A1", "A2", "A3", "A4", "A5", "A6", "A7", "S2", "C1", "C2", "C5"];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _dataRoot;
    private readonly string _dbPath;
    private readonly Dictionary<string, HashSet<string>> _fileHashes;
    private readonly ITestOutputHelper _output;

    public NdcgSensitivityProbe(ITestOutputHelper output)
    {
        _output = output;
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-ndcg-probe");
        _dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db"), _dbPath);
        (_, _fileHashes) = CorpusHashMap.Build(
            _dbPath, LoadQueries().Where(q => q.ExpectedSource is not null).Select(q => q.ExpectedSource!));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>Perturbs the query embedding by a relative epsilon and reports the resulting AdrNdcg5.</summary>
    [Fact]
    public async Task Probe_PerturbedQueryEmbedding_ReportsNdcgSensitivity()
    {
        var queries = LoadQueries()
            .Where(q => q.ExpectedSource is not null && GateQueryIds.Contains(q.Id) && q.Id.StartsWith('A'))
            .ToList();
        var inner = TestData.CreateEmbeddingService();

        _output.WriteLine($"ProcessorCount={Environment.ProcessorCount} queries={queries.Count}");
        foreach (var epsilon in new[] { 0.0, 1e-6, 1e-3, 0.01, 0.05, 0.2, 0.5 })
        {
            var store = CreateStore(new PerturbingEmbeddingService(inner, epsilon));
            var (ndcg, ranks) = await MeasureAsync(store, queries, TestContext.Current.CancellationToken);
            _output.WriteLine($"eps={epsilon:E0}  AdrNdcg5={ndcg:R}  fileRanks: {ranks}");
        }
    }

    /// <summary>Runs the identical token batch through sessions built with different intra-op thread counts.</summary>
    [Fact]
    public void Probe_OnnxThreadCount_ReportsEmbeddingDrift()
    {
        var modelPath = BundledModel.ResolveModelPath();
        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        var text = LoadQueries().First(q => q.Id == "A6").Query;
        var ids = tokenizer.EncodeToIds(text, true, true, true).ToArray();

        _output.WriteLine($"ProcessorCount={Environment.ProcessorCount} tokens={ids.Length} query=\"{text}\"");

        float[]? reference = null;
        foreach (var threads in new[] { 1, 1, 2, 4, 8 })
        {
            var vector = Embed(modelPath, ids, threads);
            if (reference is null)
            {
                reference = vector;
                _output.WriteLine($"threads={threads}: reference [{string.Join(", ", vector.Take(3).Select(v => v.ToString("R")))}]");
                continue;
            }

            var differing = 0;
            var maxAbs = 0.0;
            for (var i = 0; i < vector.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(vector[i]) != BitConverter.SingleToInt32Bits(reference[i]))
                {
                    differing++;
                }

                maxAbs = Math.Max(maxAbs, Math.Abs(vector[i] - reference[i]));
            }

            _output.WriteLine(
                $"threads={threads} vs 1: {differing}/{vector.Length} components differ bitwise, max |delta| = {maxAbs:E3}");
        }
    }

    /// <summary>Reports the fused-score gaps around the rank-5 cutoff for each gate query.</summary>
    [Fact]
    public async Task Probe_ScoreGapsAtCutoff_ReportsNearTies()
    {
        var queries = LoadQueries()
            .Where(q => q.ExpectedSource is not null && GateQueryIds.Contains(q.Id) && q.Id.StartsWith('A'))
            .ToList();
        var store = CreateStore(TestData.CreateEmbeddingService());

        foreach (var query in queries)
        {
            var results = await SearchAsync(store, query.Query, TestContext.Current.CancellationToken);
            var gaps = new List<string>();
            for (var i = 1; i < results.Count; i++)
            {
                gaps.Add((results[i - 1].Ranking - results[i].Ranking).ToString("E2"));
            }

            var relevant = _fileHashes[CorpusHashMap.FileKey(query.ExpectedSource!)];
            var hits = string.Join(",", results.Select((r, i) => relevant.Contains(r.Hash) ? (i + 1).ToString() : null)
                .Where(x => x is not null));
            _output.WriteLine($"{query.Id}: relevantRanks=[{hits}] adjacentScoreGaps=[{string.Join(" ", gaps)}]");
        }
    }

    private static float[] Embed(string modelPath, int[] ids, int threads)
    {
        using var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1 };
        using var session = new InferenceSession(modelPath, options);
        var len = ids.Length;
        var mask = new long[len];
        Array.Fill(mask, 1L);
        using var results = session.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids.Select(i => (long)i).ToArray(), [1, len])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, len])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[len], [1, len]))
        ]);
        var dense = (DenseTensor<float>)results.First(r => r.Name == "last_hidden_state").AsTensor<float>();
        var maskRow = new int[len];
        Array.Fill(maskRow, 1);
        return EmbeddingMath.MeanPoolAndNormalize(dense.Buffer.Span, maskRow, len, EmbeddingMath.Dimension);
    }

    private SqliteMemoryStore CreateStore(IEmbeddingService embeddings)
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        return TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(FixedNow), embeddings);
    }

    private static async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        SqliteMemoryStore store, string text, CancellationToken cancellationToken) =>
        await store.SearchAsync(new SearchQuery(
            ProjectId, text, SearchScope.Project,
            Limit: SearchLimit, MinScore: 0.0, RrfK: 60, FtsWeight: 1, VectorWeight: 1,
            SourceLambda: 0.1, ConsolidationThreshold: 0.1,
            DocScoreFormula: DocScoreFormula.Max), cancellationToken);

    private async Task<(double Ndcg, string Ranks)> MeasureAsync(
        SqliteMemoryStore store, IReadOnlyList<BaselineQuery> queries, CancellationToken cancellationToken)
    {
        var scores = new List<double>();
        var ranks = new List<string>();
        foreach (var query in queries)
        {
            var results = await SearchAsync(store, query.Query, cancellationToken);
            var relevant = _fileHashes[CorpusHashMap.FileKey(query.ExpectedSource!)];
            var hashes = results.Select(r => r.Hash).ToList();
            scores.Add(RetrievalMetrics.NdcgAtK(hashes, relevant, RankCutoff));
            var first = hashes.FindIndex(relevant.Contains);
            ranks.Add($"{query.Id}:{(first < 0 ? "-" : (first + 1).ToString())}");
        }

        return (scores.Average(), string.Join(" ", ranks));
    }

    private static BaselineQuery[] LoadQueries() =>
        JsonSerializer.Deserialize<BaselineQuery[]>(
            File.ReadAllText(Path.Combine(FindProjectRoot(), "scripts", "baseline-queries.json")), JsonOptions) ?? [];

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private sealed record BaselineQuery(
        string Id, string Category, string Query, string? ExpectedSource,
        string? ExpectedKnowledge, string Scope, int SearchLimit, bool NegativeTest);

    /// <summary>Wraps the real engine and nudges each component by a deterministic relative epsilon.</summary>
    private sealed class PerturbingEmbeddingService(IEmbeddingService inner, double epsilon) : IEmbeddingService
    {
        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) =>
            new Generator(inner.CreateGenerator(settings), epsilon);

        private sealed class Generator(IEmbeddingGenerator<string, Embedding<float>> inner, double epsilon)
            : IEmbeddingGenerator<string, Embedding<float>>
        {
            public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
                IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var generated = await inner.GenerateAsync(values, options, cancellationToken);
                if (epsilon == 0.0)
                {
                    return generated;
                }

                var perturbed = new GeneratedEmbeddings<Embedding<float>>(generated.Count);
                foreach (var embedding in generated)
                {
                    var vector = embedding.Vector.ToArray();
                    for (var i = 0; i < vector.Length; i++)
                    {
                        vector[i] = (float)(vector[i] * (1.0 + ((i % 2 == 0 ? 1.0 : -1.0) * epsilon)));
                    }

                    perturbed.Add(new Embedding<float>(vector));
                }

                return perturbed;
            }

            public void Dispose()
            {
            }

            public object? GetService(Type serviceType, object? serviceKey) => null;
        }
    }
}
