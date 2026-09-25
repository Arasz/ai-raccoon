using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AiRaccoon.Infrastructure.Embedding.NeuralEngine;

/// <summary>Builds the CoreML bucket sessions over the ANE graph and the CPU session for longer rows (ADR-0118).</summary>
internal sealed class CoreMlSessionFactory : ICoreMlSessionFactory
{
    private readonly string _graphPath;
    private readonly string _productGraphPath;
    private readonly IEmbeddingTokenizer _tokenizer;
    private readonly EngineDescriptor _descriptor;
    private readonly int _intraOpThreads;
    private readonly ILogger _logger;

    public CoreMlSessionFactory(string graphPath, string productGraphPath, IEmbeddingTokenizer tokenizer,
        EngineDescriptor descriptor, int intraOpThreads, ILogger logger)
    {
        Guard.IsNotNullOrWhiteSpace(graphPath);
        Guard.IsNotNullOrWhiteSpace(productGraphPath);
        Guard.IsNotNull(tokenizer);
        Guard.IsNotNull(descriptor);
        Guard.IsNotNullOrWhiteSpace(descriptor.EmbeddingOutput);
        Guard.IsNotNull(logger);
        _graphPath = graphPath;
        _productGraphPath = productGraphPath;
        _tokenizer = tokenizer;
        _descriptor = descriptor;
        _intraOpThreads = intraOpThreads;
        _logger = logger;
    }

    /// <summary>The CoreML provider options for one bucket: an ML Program on the CPU and Neural Engine, static shapes, its own cache.</summary>
    internal static Dictionary<string, string> ProviderOptions(string cacheDirectory) => new()
    {
        ["ModelFormat"] = "MLProgram",
        ["MLComputeUnits"] = "CPUAndNeuralEngine",
        ["RequireStaticInputShapes"] = "1",
        ["ModelCacheDirectory"] = cacheDirectory
    };

    public ICoreMlBucketSession CreateBucket(int bucket, string cacheDirectory)
    {
        Directory.CreateDirectory(cacheDirectory);
        using var options = new SessionOptions();
        if (_intraOpThreads > 0)
        {
            options.IntraOpNumThreads = _intraOpThreads;
        }

        options.AddFreeDimensionOverrideByName(CoreMlGraph.BatchDimension, 1);
        options.AddFreeDimensionOverrideByName(CoreMlGraph.SequenceDimension, bucket);
        options.AddFreeDimensionOverrideByName(CoreMlGraph.MaskSequenceDimension, bucket);
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        options.AppendExecutionProvider("CoreML", ProviderOptions(cacheDirectory));
        return new CoreMlBucketSession(new InferenceSession(_graphPath, options), _descriptor.EmbeddingOutput!);
    }

    public ILocalEmbeddingGenerator CreateOverflow() =>
        new OnnxEmbeddingGenerator(_productGraphPath, _tokenizer, _descriptor, _logger, _intraOpThreads);
}

/// <summary>One CoreML session compiled for a single padded row length.</summary>
internal sealed class CoreMlBucketSession(InferenceSession session, string outputName) : ICoreMlBucketSession
{
    public float[] Run(long[] inputIds, long[] attentionMask)
    {
        var feed = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, inputIds.Length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, attentionMask.Length]))
        };
        using var results = session.Run(feed);
        return [.. results.First(r => r.Name == outputName).AsTensor<float>()];
    }

    public void Dispose() => session.Dispose();
}
