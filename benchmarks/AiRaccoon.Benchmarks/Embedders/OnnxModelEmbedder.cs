using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
///     ONNX-based embeddings via AiRaccoon's infrastructure <see cref="OnnxEmbeddingGenerator" />.
///     Two construction paths: a manifest model directory (containing ai-raccoon.manifest.json) or the
///     bundled ONNX model (model_qint8_arm64.onnx + vocab.txt) with the legacy wordpiece tokenizer.
/// </summary>
public sealed class OnnxModelEmbedder : EmbeddingBackend
{
    private readonly string _name;

    /// <summary>Creates from a manifest-based model directory.</summary>
    /// <param name="modelDirectory">
    ///     Path to the model directory containing ai-raccoon.manifest.json, the ONNX file,
    ///     and the tokenizer files.
    /// </param>
    public OnnxModelEmbedder(string modelDirectory)
        : base(CreateFromManifest(modelDirectory, out var name))
    {
        _name = name;
    }

    /// <summary>Creates from the bundled ONNX model with a wordpiece tokenizer.</summary>
    /// <param name="modelPath">Path to the .onnx model file.</param>
    /// <param name="vocabPath">Path to the BERT wordpiece vocab.txt.</param>
    public OnnxModelEmbedder(string modelPath, string vocabPath)
        : base(CreateBundled(modelPath, vocabPath, out var name))
    {
        _name = name;
    }

    protected override string BackendName => _name;

    private static IEmbeddingGenerator<string, Embedding<float>> CreateFromManifest(
        string modelDirectory, out string name)
    {
        var serializer = new EmbeddingManifestSerializer();
        var validator = new EmbeddingManifestValidator();
        var loader = new EmbeddingManifestLoader(serializer, validator);
        var descriptor = loader.Load(modelDirectory);
        var tokenizer = new EmbeddingTokenizerFactory().Create(descriptor, modelDirectory);
        var generator = new OnnxEmbeddingGenerator(
            Path.Combine(modelDirectory, descriptor.OnnxModelFile),
            tokenizer,
            descriptor,
            NullLogger.Instance);

        name = $"onnx:manifest:{descriptor.Model}";
        return generator;
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateBundled(
        string modelPath, string vocabPath, out string name)
    {
        var tokenizer = WordPieceEmbeddingTokenizer.Create(vocabPath);
        var modelFileName = Path.GetFileName(modelPath);
        var modelName = Path.GetFileNameWithoutExtension(modelPath);

        var descriptor = new EngineDescriptor(
            $"{modelName} (bundled wordpiece)",
            "sentence-transformers/all-MiniLM-L6-v2",
            null,
            384,
            256,
            "l2",
            "mean",
            2,
            "bert-wordpiece",
            "vocab.txt",
            null,
            true,
            ["input_ids", "attention_mask", "token_type_ids"],
            "last_hidden_state",
            null,
            modelFileName,
            []);

        var generator = new OnnxEmbeddingGenerator(
            modelPath, tokenizer, descriptor, NullLogger.Instance);

        name = $"onnx:bundled:{modelName}";
        return generator;
    }
}