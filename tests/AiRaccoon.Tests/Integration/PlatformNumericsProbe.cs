using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Retrieval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Reproduces docs/adr/0049: prints the host's CPU capability alongside the ONNX
///     query-embedding fingerprint and the sweep's AdrNdcg5, so a run reports which hardware
///     produced which number. Diagnostic, not a gate — env-gated and skipped by default.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PlatformNumericsProbe : IDisposable
{
    private const string RunEnvVar = "AIRACCOON_PLATFORM_PROBE";
    private const string ProjectId = "job-search-ai-assistant";
    private const int SearchLimit = 10;
    private const int RankCutoff = 5;

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] GateQueryIds =
        ["A1", "A2", "A3", "A4", "A5", "A6", "A7"];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ITestOutputHelper _output;
    private string? _dataRoot;
    private Dictionary<string, HashSet<string>> _fileHashes = [];

    public PlatformNumericsProbe(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (_dataRoot is not null)
        {
            TestData.DeleteTempRoot(_dataRoot);
        }
    }

    /// <summary>Copies the corpus fixture into a private root and derives the relevance sets.</summary>
    private void Setup()
    {
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-platform-probe");
        var dbPath = Path.Combine(_dataRoot, "memory.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "jsaa-memory.db"), dbPath);
        (_, _fileHashes) = CorpusHashMap.Build(
            dbPath, LoadQueries().Where(q => q.ExpectedSource is not null).Select(q => q.ExpectedSource!));
    }

    [Fact]
    public async Task Probe_HostFingerprint_ReportsCpuAndEmbeddingAndNdcg()
    {
        if (Environment.GetEnvironmentVariable(RunEnvVar) != "1")
        {
            Assert.Skip($"{RunEnvVar} not set — this probe reports host CPU capability, the ONNX " +
                        "query-embedding fingerprint and AdrNdcg5 together (docs/adr/0049). It asserts " +
                        $"nothing; set {RunEnvVar}=1 to run it.");
            return;
        }

        Setup();
        _output.WriteLine("=== HOST ===");
        _output.WriteLine($"OS={RuntimeInformation.OSDescription}");
        _output.WriteLine($"Arch={RuntimeInformation.ProcessArchitecture} ProcessorCount={Environment.ProcessorCount}");
        _output.WriteLine($"Avx2={Avx2.IsSupported} Avx512F={Avx512F.IsSupported} " +
                          $"Avx512BW={Avx512BW.IsSupported} AvxVnni={AvxVnni.IsSupported} " +
                          $"Sse41={Sse41.IsSupported} AdvSimd={AdvSimd.IsSupported} Dp={Dp.IsSupported}");
        _output.WriteLine($"VectorFloatCount={Vector<float>.Count}");
        if (File.Exists("/proc/cpuinfo"))
        {
            var model = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            var flags = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("flags", StringComparison.Ordinal)) ?? "";
            _output.WriteLine($"cpuinfo {model}");
            _output.WriteLine("vnni-ish flags: " + string.Join(" ", flags.Split(' ')
                .Where(f => f.Contains("vnni", StringComparison.Ordinal) || f.Contains("avx", StringComparison.Ordinal))));
        }

        _output.WriteLine("=== EMBEDDING FINGERPRINT ===");
        var modelPath = BundledModel.ResolveModelPath();
        var tokenizer = OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath());
        foreach (var query in LoadQueries().Where(q => GateQueryIds.Contains(q.Id)))
        {
            var ids = tokenizer.EncodeToIds(query.Query, true, true, true).ToArray();
            var vector = Embed(modelPath, ids);
            var bytes = new byte[vector.Length * 4];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            _output.WriteLine(
                $"{query.Id}: sha256={Convert.ToHexString(SHA256.HashData(bytes))[..16]} " +
                $"v[0..3]=[{string.Join(",", vector.Take(3).Select(v => v.ToString("R")))}]");
        }

        _output.WriteLine("=== RETRIEVAL ===");
        var options = new InfrastructureOptions { DataRoot = _dataRoot!, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());

        var scores = new List<double>();
        foreach (var query in LoadQueries().Where(q => GateQueryIds.Contains(q.Id) && q.ExpectedSource is not null))
        {
            var results = (await store.SearchAsync(new SearchQuery(
                ProjectId, query.Query, SearchScope.Project,
                Limit: SearchLimit, MinRelativeScore: 0.0, RrfK: 60, FtsWeight: 1, VectorWeight: 1,
                SourceLambda: 0.1, ConsolidationThreshold: 0.1,
                DocScoreFormula: DocScoreFormula.Max), TestContext.Current.CancellationToken)).Results;
            var relevant = _fileHashes[CorpusHashMap.FileKey(query.ExpectedSource!)];
            var hashes = results.Select(r => r.Hash).ToList();
            var ndcg = RetrievalMetrics.NdcgAtK(hashes, relevant, RankCutoff);
            scores.Add(ndcg);
            var hits = string.Join(",", hashes.Select((h, i) => relevant.Contains(h) ? (i + 1).ToString() : null)
                .Where(x => x is not null));
            _output.WriteLine($"{query.Id}: ndcg@5={ndcg:F4} relevantRanks=[{hits}] " +
                              $"top3Scores=[{string.Join(",", results.Take(3).Select(r => r.Ranking.ToString("F6")))}]");
        }

        _output.WriteLine($"=== AdrNdcg5 = {scores.Average():R} ===");
    }

    private static float[] Embed(string modelPath, int[] ids)
    {
        using var session = new InferenceSession(modelPath);
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
        return EmbeddingMath.MeanPoolAndNormalize(dense.Buffer.Span, maskRow, len, 384);
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
        string Id,
        string Category,
        string Query,
        string? ExpectedSource,
        string? ExpectedKnowledge,
        string Scope,
        int SearchLimit,
        bool NegativeTest);
}
