using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Regenerates tests/AiRaccoon.Tests/Resources/gate-query-vectors.json from the live bundled
///     model (docs/adr/0050). Env-gated: it overwrites a committed fixture whose values are
///     host-arithmetic-specific (docs/adr/0049), so it must be run deliberately and its output
///     reviewed, never on CI.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class GateQueryVectorRegenerationTool(ITestOutputHelper output)
{
    public const string RunEnvVar = "AIRACCOON_REGENERATE_GATE_QUERY_VECTORS";

    private const string FixtureNote =
        "NOT ground truth. These are the bundled u8s8-quantized model's outputs on ONE arithmetic " +
        "path; a VNNI x64 host and a non-VNNI x64 host each produce materially different vectors " +
        "from the same model (docs/adr/0049). They are pinned so the retrieval sweeps measure " +
        "fusion and ranking configuration instead of the host CPU (docs/adr/0050). Which of the " +
        "three paths is 'correct' is an open product question — do not treat these as the answer.";

    [Fact]
    public async Task RegenerateGateQueryVectors()
    {
        if (Environment.GetEnvironmentVariable(RunEnvVar) != "1")
        {
            Assert.Skip($"{RunEnvVar} not set — this tool overwrites the committed " +
                        $"tests/AiRaccoon.Tests/Resources/{PinnedQueryVectors.FileName} fixture with vectors " +
                        $"from this host's arithmetic path (docs/adr/0049, docs/adr/0050). Set {RunEnvVar}=1 to run it.");
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var ensured = await TestData.CreateBundledModel().EnsureAsync(cancellationToken);
        ensured.AllPresent.ShouldBeTrue($"Bundled embedding model missing: {string.Join("; ", ensured.Errors)}");

        var queries = BaselineQueryCatalog.Load();
        queries.ShouldNotBeEmpty();

        using var generator = TestData.CreateEmbeddingService()
            .CreateGenerator(new EmbeddingSettings("local", null, null, null));

        var vectors = new List<PinnedQueryVector>(queries.Count);
        foreach (var query in queries)
        {
            var result = await generator.GenerateAsync([query.Query], cancellationToken: cancellationToken);
            var encoded = PinnedQueryVectors.Encode(result[0].Vector.Span);
            vectors.Add(new PinnedQueryVector(query.Id, query.Query, PinnedQueryVectors.Fingerprint(encoded), encoded));
        }

        var file = new PinnedQueryVectorFile(
            FixtureNote,
            DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DescribeArithmeticPath(),
            RuntimeInformation.FrameworkDescription,
            BundledModel.ModelSha256,
            BundledModel.VocabSha256,
            EmbeddingDimension,
            vectors);

        var path = PinnedQueryVectors.Write(file, PinnedQueryVectors.SourcePath);
        output.WriteLine($"wrote {vectors.Count} pinned query vectors to {path}");
        output.WriteLine($"arithmetic path: {file.ArithmeticPath}");
        foreach (var vector in vectors)
        {
            output.WriteLine($"  {vector.Id}: sha256={vector.Sha256[..16]}");
        }
    }

    /// <summary>384, the bundled all-MiniLM-L6-v2 output width.</summary>
    private const int EmbeddingDimension = 384;

    /// <summary>The ISA facts that decide which u8s8 kernel ONNX Runtime picks (docs/adr/0049).</summary>
    private static string DescribeArithmeticPath() =>
        $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}; " +
        $"AdvSimd={AdvSimd.IsSupported} Dp={Dp.IsSupported} Avx2={Avx2.IsSupported} " +
        $"Avx512F={Avx512F.IsSupported} AvxVnni={AvxVnni.IsSupported}";
}
