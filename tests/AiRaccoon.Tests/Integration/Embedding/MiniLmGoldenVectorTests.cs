using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     G3 golden-vector gate (docs/work/2026-08-21-arbitrary-embedding-models-plan.md): MiniLM
///     embeddings of the eval-set-100 query texts captured from the CURRENT generator and committed,
///     then re-embedded after the WP3 engine generalization. Pass condition: same-arch
///     byte-identical vectors plus the tolerance secondaries (L2 ≤ 1e-6 and token-id equality).
///     Byte-identity is the tripwire; the tolerance assertions are the pass condition.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MiniLmGoldenVectorTests : IAsyncLifetime
{
    private const string CaptureEnvVar = "AIRACCOON_GOLDEN_CAPTURE";

    private const string GoldenRelativePath = "tests/AiRaccoon.Tests/Resources/goldens/minilm-eval-set-100.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async ValueTask InitializeAsync() =>
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    ///     One-shot capture: embeds the eval-set-100 query texts with the generator as built today
    ///     and writes the golden file. Skips unless AIRACCOON_GOLDEN_CAPTURE=1 — the committed
    ///     goldens are only ever rewritten deliberately (new arch or a plan-approved behavior change).
    /// </summary>
    [Fact]
    public async Task CaptureGoldenVectors()
    {
        if (Environment.GetEnvironmentVariable(CaptureEnvVar) != "1")
        {
            Assert.Skip($"set {CaptureEnvVar}=1 to regenerate the committed goldens");
        }

        var queries = ReadEvalSetQueries();
        var service = TestData.CreateEmbeddingService();
        using var generator = service.CreateGenerator(new EmbeddingSettings("local", null, null, null));

        var entries = new List<GoldenEntry>(queries.Count);
        foreach (var (id, query) in queries)
        {
            var result = await generator.GenerateAsync([query],
                cancellationToken: TestContext.Current.CancellationToken);
            var vector = result[0].Vector.ToArray();
            entries.Add(new GoldenEntry(id, query, ToBase64(vector), EncodeTokenIds(query)));
        }

        var golden = new GoldenFile(new GoldenProvenance(
            CapturedUtc: DateTimeOffset.UtcNow.ToString("O"),
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            OsDescription: RuntimeInformation.OSDescription,
            BuildVersion: typeof(EmbeddingService).Assembly.GetName().Version?.ToString() ?? "unknown",
            Engine: "OnnxEmbeddingGenerator — bundled all-MiniLM-L6-v2 qint8, mean-pool + L2, 256 window (pre-WP3 refactor)",
            ModelSha256: BundledModel.ModelSha256,
            VocabSha256: BundledModel.VocabSha256,
            CaptureCommand: "dotnet test --filter \"FullyQualifiedName~MiniLmGoldenVectorTests.CaptureGoldenVectors\""),
            entries);

        var path = Path.Combine(FindRepoRoot(), GoldenRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(golden, JsonOptions),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The gate: every committed golden vector must come back byte-identical (bit-for-bit) from
    ///     the refactored engine, within the tolerance secondaries (L2 ≤ 1e-6, token-id equality).
    /// </summary>
    [Fact]
    public async Task BundledMiniLm_Embeddings_MatchCommittedGoldens()
    {
        var goldenPath = Path.Combine(FindRepoRoot(), GoldenRelativePath);
        File.Exists(goldenPath).ShouldBeTrue(
            $"golden file {goldenPath} missing — run the capture once on the pre-refactor build (AIRACCOON_GOLDEN_CAPTURE=1)");
        var golden = JsonSerializer.Deserialize<GoldenFile>(await File.ReadAllTextAsync(goldenPath,
            TestContext.Current.CancellationToken))!;
        golden.Entries.Count.ShouldBe(100, "eval-set-100 has exactly 100 queries");

        // The capture records its architecture so this comparison can be made; see the loop below.
        var sameArchitecture = string.Equals(golden.Provenance.ProcessArchitecture,
            RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.Ordinal);

        var service = TestData.CreateEmbeddingService();
        using var generator = service.CreateGenerator(new EmbeddingSettings("local", null, null, null));

        foreach (var entry in golden.Entries)
        {
            var result = await generator.GenerateAsync([entry.Query],
                cancellationToken: TestContext.Current.CancellationToken);
            var actual = result[0].Vector.ToArray();

            var expected = FromBase64(entry.Bytes);
            actual.Length.ShouldBe(expected.Length, $"entry {entry.Id}: dimension drift");

            // Byte-identity is the tripwire and it only means anything on the capture's own
            // architecture (G3, review M2/F9): ONNX Runtime's float results are bit-stable per arch,
            // not across them, so asserting it on a different arch reports expected FP
            // non-determinism as a refactor regression. The tolerance secondaries below are the pass
            // condition and run everywhere — a real behaviour change moves L2 far past 1e-6.
            if (sameArchitecture)
            {
                var mismatchedBits = 0;
                for (var i = 0; i < expected.Length; i++)
                {
                    if (BitConverter.SingleToInt32Bits(actual[i]) != BitConverter.SingleToInt32Bits(expected[i]))
                    {
                        mismatchedBits++;
                    }
                }

                mismatchedBits.ShouldBe(0,
                    $"entry {entry.Id}: {mismatchedBits}/{expected.Length} float32 values differ bit-for-bit from the golden capture — the WP3 refactor changed engine output");
            }

            // Numeric secondary. 1e-6 is a SAME-ARCH bound: the bundled model is qint8, and x64
            // (AVX-VNNI) and ARM (SDOT) requantise differently, so a few 0.1-L2 drifts are the
            // architecture, not the refactor — measured 0.1189 on x64 CI against an Arm64 capture
            // that the same build reproduces bit-for-bit. Off the capture arch the honest bound is a
            // similarity floor; the exact check that still holds everywhere is the token-id assertion
            // below, which is what a broken tokenizer seam would actually trip.
            var l2 = Math.Sqrt(expected.Zip(actual, (a, b) => (double)(a - b) * (a - b)).Sum());
            if (sameArchitecture)
            {
                l2.ShouldBeLessThanOrEqualTo(1e-6, $"entry {entry.Id}: L2 drift {l2} exceeds the 1e-6 tolerance");
            }
            else
            {
                // A COARSE BREAKAGE FLOOR, not a drift bound. Measured cross-arch requantisation on
                // this qint8 model reaches cosine 0.9895 (E008 on x64 vs the Arm64 capture), so a
                // tight bound here would only be a constant fitted to whatever CI happened to
                // produce. 0.95 is far below the observed drift and far above a real behaviour
                // change — wrong pooling or wrong special tokens lands in the 0.3-0.7 range. The
                // exact cross-arch check is the token-id assertion below; restoring a tight vector
                // bound on CI needs a per-architecture golden capture.
                var cosine = Cosine(expected, actual);
                cosine.ShouldBeGreaterThanOrEqualTo(0.95,
                    $"entry {entry.Id}: cosine {cosine} against the golden capture — far below cross-architecture "
                    + "requantisation drift; the engine's behaviour changed");
            }

            // Token-id secondary: the tokenizer seam must reproduce the pinned EncodeToIds(text, true, true, true) ids.
            var ids = EncodeTokenIds(entry.Query);
            ids.ShouldBe(entry.TokenIds, $"entry {entry.Id}: token ids differ from the golden capture");
        }
    }

    /// <summary>
    ///     The seam through which golden token ids are produced. Pre-refactor this was the raw
    ///     BertTokenizer with the pinned overload; post-refactor it is the IEmbeddingTokenizer
    ///     wordpiece implementation — the swap is itself what the token-id assertion pins.
    /// </summary>
    private static IReadOnlyList<int> EncodeTokenIds(string text) =>
        WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()).EncodeToIds(text, addSpecialTokens: true);

    private static IReadOnlyList<(string Id, string Query)> ReadEvalSetQueries()
    {
        var corpusPath = Path.Combine(FindRepoRoot(), "scripts/retrieval_tuning/corpora/eval-set-100.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(corpusPath));
        return doc.RootElement.EnumerateArray()
            .Select(item => (item.GetProperty("id").GetString()!, item.GetProperty("query").GetString()!))
            .ToList();
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "retrieval_tuning", "corpora", "eval-set-100.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repo root not found above the test output directory");
    }

    private static string ToBase64(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Cosine similarity — the arch-independent shape check when exact bounds cannot hold.</summary>
    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static float[] FromBase64(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private sealed record GoldenFile(GoldenProvenance Provenance, IReadOnlyList<GoldenEntry> Entries);

    private sealed record GoldenProvenance(
        string CapturedUtc,
        string ProcessArchitecture,
        string OsDescription,
        string BuildVersion,
        string Engine,
        string ModelSha256,
        string VocabSha256,
        string CaptureCommand);

    private sealed record GoldenEntry(string Id, string Query, string Bytes, IReadOnlyList<int> TokenIds);
}
