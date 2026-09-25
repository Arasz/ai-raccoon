namespace AiRaccoon.Infrastructure.Embedding.NeuralEngine;

/// <summary>Builds the sessions a <see cref="NeuralEngineEmbeddingGenerator" /> swaps to.</summary>
internal interface ICoreMlSessionFactory
{
    /// <summary>A CoreML session compiled for rows padded to <paramref name="bucket" /> tokens, caching into <paramref name="cacheDirectory" />. Blocks for the whole compile.</summary>
    ICoreMlBucketSession CreateBucket(int bucket, string cacheDirectory);

    /// <summary>A CPU session over the product graph, for rows longer than the largest bucket.</summary>
    ILocalEmbeddingGenerator CreateOverflow();
}

/// <summary>One fixed-shape CoreML session: batch 1, a row already padded to its bucket.</summary>
internal interface ICoreMlBucketSession : IDisposable
{
    /// <summary>The row's pooled, unnormalized embedding.</summary>
    float[] Run(long[] inputIds, long[] attentionMask);
}
