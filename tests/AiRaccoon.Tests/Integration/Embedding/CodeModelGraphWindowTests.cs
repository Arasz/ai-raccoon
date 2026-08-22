using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The measurement #422 turned on, kept runnable. `CodeChunker.DefaultBudget` and the
///     activation gate both descend from one claim about code-daemon-embed-v1's ONNX graph — that
///     it accepts 512 tokens, not the 128 the exploration spike recorded — and a claim that only
///     ever lived in prose is how #422 happened in the first place. This re-derives it from the
///     weights: the largest sequence the graph runs, and whether content past token 128 reaches
///     the output at all.
///     <para>
///         Needs the real 187 MB model on disk, so it is not a CI dependency: point
///         <see cref="ModelDirEnvVar" /> at a directory holding the downloaded model plus its
///         <see cref="EmbeddingManifest.FileName" />, or the test skips.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class CodeModelGraphWindowTests
{
    /// <summary>Absolute path to a downloaded <c>faxenoff/code-daemon-embed-v1</c> directory.</summary>
    internal const string ModelDirEnvVar = "AIRACCOON_CODE_MODEL_DIR";

    /// <summary>
    ///     The graph runs the whole budget and one token past it fails: the window is exactly what
    ///     the manifest declares, so a chunk sized to <see cref="CodeChunker.DefaultBudget" /> plus
    ///     its two special tokens is the largest thing the engine will ever be handed.
    /// </summary>
    [Fact]
    public void TheGraph_RunsTheFullManifestWindow_AndFailsOneTokenPastIt()
    {
        var descriptor = LoadDescriptorOrSkip(out var modelDirectory);
        using var session = new InferenceSession(Path.Combine(modelDirectory, descriptor.OnnxModelFile));

        descriptor.ContextWindowTokens.ShouldBe(512,
            "the repo's config.json declares max_position_embeddings 514 and positions start at padding_idx + 1");
        CodeChunker.DefaultBudget.ShouldBe(descriptor.ContextWindowTokens - descriptor.SpecialTokenReservation,
            "the chunk budget is the measured window minus the <s>/</s> reservation, not a prose constant");

        Run(session, IdsOfLength(descriptor.ContextWindowTokens)).Length.ShouldBe(descriptor.Dimensions,
            $"{descriptor.ContextWindowTokens} tokens is inside the window and must embed");
        // One token past the window must FAIL on the position-embedding Gather — a graph that
        // silently truncated instead is the world the spike's 128 assumed, and it would leave a
        // too-long chunk embedded from its prefix with nothing saying so.
        var overrun = Should.Throw<OnnxRuntimeException>(
            () => Run(session, IdsOfLength(descriptor.ContextWindowTokens + 1)));
        overrun.Message.ShouldContain("position_embeddings");
    }

    /// <summary>
    ///     The spike's "hard 128-token cap" would mean everything past token 128 is ignored, which
    ///     would make both cosines below exactly 1. They are not: the graph attends to the whole
    ///     sequence, so a 126-token budget was throwing away model capacity rather than respecting it.
    /// </summary>
    [Fact]
    public void TheGraph_ReadsContentPastToken128()
    {
        var descriptor = LoadDescriptorOrSkip(out var modelDirectory);
        using var session = new InferenceSession(Path.Combine(modelDirectory, descriptor.OnnxModelFile));

        var full = IdsOfLength(300);
        var differentTail = (long[])full.Clone();
        for (var i = 128; i < differentTail.Length - 1; i++)
        {
            differentTail[i] = 500 + (i % 97);
        }

        Cosine(Run(session, full), Run(session, differentTail)).ShouldBeLessThan(0.99,
            "two sequences sharing only their first 128 tokens must not embed identically");
        Cosine(Run(session, full), Run(session, [.. full.Take(128)])).ShouldBeLessThan(0.99,
            "a sequence and its own 128-token prefix must not embed identically");
    }

    private static EngineDescriptor LoadDescriptorOrSkip(out string modelDirectory)
    {
        var configured = Environment.GetEnvironmentVariable(ModelDirEnvVar);
        if (string.IsNullOrWhiteSpace(configured) || !Directory.Exists(configured))
        {
            Assert.Skip($"set {ModelDirEnvVar} to a downloaded faxenoff/code-daemon-embed-v1 directory "
                        + $"('{CodeEngineSetup.DefaultModelCommand}' puts one at <data-root>/models/); "
                        + "the weights are too large to ship with the suite");
        }

        modelDirectory = Path.GetFullPath(configured);
        return new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(modelDirectory);
    }

    /// <summary>A well-formed sequence of the requested length: real id range, framed by the model's own <c>&lt;s&gt;</c>/<c>&lt;/s&gt;</c>.</summary>
    private static long[] IdsOfLength(int length)
    {
        var random = new Random(7);
        var ids = new long[length];
        ids[0] = 2;
        for (var i = 1; i < length - 1; i++)
        {
            ids[i] = random.Next(4, 22739);
        }

        ids[length - 1] = 3;
        return ids;
    }

    private static float[] Run(InferenceSession session, long[] ids)
    {
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);
        using var results = session.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, ids.Length]))
        ]);
        return results[0].AsTensor<float>().ToArray();
    }

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
}
