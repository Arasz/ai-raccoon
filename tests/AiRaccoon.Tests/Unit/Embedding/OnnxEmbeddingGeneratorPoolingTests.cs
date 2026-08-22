using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.AI;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Issue #466: a manifest's <c>pooling.mode</c> is a guess whenever the repo ships no
///     sentence-transformers pooling config (<c>ModelDownloadPlanner</c>'s "placeholder(wp5)"
///     branch defaults to <c>cls</c>), but the ONNX output's RANK is not a guess — a rank-2
///     <c>[batch, dimensions]</c> output is already the embedding, whatever the manifest calls it.
///     faxenoff/code-daemon-embed-v1 is exactly that shape ("pooled AND L2-normalized inside the
///     graph", its model card) and the rank-3 arithmetic the declared <c>cls</c> selected read past
///     the end of the buffer, so every embed threw a bare ArgumentOutOfRangeException.
///     Runs with no model on disk: <see cref="OnnxEmbeddingGenerator.Pool" /> takes the session's
///     output buffer and its dimensions, which a test can hand it directly.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OnnxEmbeddingGeneratorPoolingTests
{
    private const int Dimension = 4;

    /// <summary>Two already-pooled rows: [1,0,0,0] and [0,2,0,0], the shape a graph that pools itself emits.</summary>
    private static readonly float[] PooledOutput = [1, 0, 0, 0, 0, 2, 0, 0];

    [Fact]
    public void Pool_GraphAlreadyPooledButManifestDeclaresCls_PoolsThePooledOutputInsteadOfThrowing()
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();

        OnnxEmbeddingGenerator.Pool(PooledOutput, [2, Dimension], batch: 2, maxLen: 14, Dimension,
            AttentionMask(batch: 2, maxLen: 14), pooling: "cls", normalization: "l2", "last_hidden_state", embeddings);

        embeddings.Count.ShouldBe(2, "one vector per batch row");
        embeddings[0].Vector.ToArray().ShouldBe([1f, 0f, 0f, 0f]);
        embeddings[1].Vector.ToArray().ShouldBe([0f, 1f, 0f, 0f], "normalization=l2 still applies to the graph's own vector");
    }

    [Fact]
    public void Pool_GraphAlreadyPooledAndManifestDeclaresMean_PoolsThePooledOutput()
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();

        OnnxEmbeddingGenerator.Pool(PooledOutput, [2, Dimension], batch: 2, maxLen: 14, Dimension,
            AttentionMask(batch: 2, maxLen: 14), pooling: "mean", normalization: "none", "last_hidden_state", embeddings);

        embeddings.Count.ShouldBe(2);
        embeddings[1].Vector.ToArray().ShouldBe([0f, 2f, 0f, 0f], "normalization=none leaves the graph's vector alone");
    }

    /// <summary>The bundled MiniLM shape: a real token-embeddings output still gets pooled here.</summary>
    [Fact]
    public void Pool_TokenLevelOutput_StillPoolsWithTheManifestsMode()
    {
        // one row, two tokens: the [CLS] row is [1,0,0,0] and the second token's row is [0,0,0,3].
        float[] tokenEmbeddings = [1, 0, 0, 0, 0, 0, 0, 3];
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();

        OnnxEmbeddingGenerator.Pool(tokenEmbeddings, [1, 2, Dimension], batch: 1, maxLen: 2, Dimension,
            AttentionMask(batch: 1, maxLen: 2), pooling: "cls", normalization: "none", "last_hidden_state", embeddings);

        embeddings.ShouldHaveSingleItem().Vector.ToArray().ShouldBe([1f, 0f, 0f, 0f],
            "cls pooling over a rank-3 output reads the first token's row, unchanged by this fix");
    }

    [Fact]
    public void Pool_ModelOutputPoolingOverATokenLevelOutput_RefusesActionably()
    {
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();

        var ex = Should.Throw<InvalidOperationException>(() => OnnxEmbeddingGenerator.Pool(
            [1, 0, 0, 0, 0, 0, 0, 3], [1, 2, Dimension], batch: 1, maxLen: 2, Dimension,
            AttentionMask(batch: 1, maxLen: 2), pooling: "model-output", normalization: "none",
            "sentence_embedding", embeddings));

        ex.Message.ShouldContain("sentence_embedding");
        ex.Message.ShouldContain("model-output pooling");
    }

    private static long[] AttentionMask(int batch, int maxLen)
    {
        var mask = new long[batch * maxLen];
        Array.Fill(mask, 1L);
        return mask;
    }
}
