using System.Text.Json.Serialization;

namespace AiRaccoon.Infrastructure.Embedding.Manifest;

/// <summary>Tokenizer families the manifest contract knows (D5: bert-wordpiece + sentencepiece
/// required; tokenizer-json gated on an ML.Tokenizers capability check and deferred).</summary>
public enum TokenizerFamily
{
    BertWordpiece,
    [SchemaName("sentencepiece")] SentencePiece,
    [SchemaName("tokenizer-json")] TokenizerJson
}

/// <summary>
///     Pooling strategies the engine can apply (D1): <see cref="ModelOutput" /> consumes a
///     graph-computed pooled output (e.g. bge-m3's <c>sentence_embedding</c>); the others pool
///     the token-level output client-side. <see cref="LastToken" /> is pass-through only — no
///     consumer in scope.
/// </summary>
public enum PoolingMode
{
    Mean,
    Cls,
    [SchemaName("model-output")] ModelOutput,
    [SchemaName("last-token")] LastToken
}

public enum NormalizationMode
{
    L2,
    None
}

/// <summary>Engine kind the manifest describes; must match the settings row (validation).</summary>
public enum ManifestProvider
{
    Local,
    Openai
}

/// <summary>
///     The pinned v1 manifest contract (plan D1, architecture §5.6): a sidecar manifest.json next
///     to a local model's files. Every field below is part of the pinned schema — the golden
///     fixtures in tests/AiRaccoon.Tests/Resources/ManifestFixtures pin it. No runtime use before
///     WP3 (lane B consumes these records); WP1 ships the contract, serializer and validation only.
/// </summary>
public sealed record EmbeddingManifest(
    [property: JsonRequired] int ManifestVersion,
    [property: JsonRequired] string Model,
    [property: JsonRequired] ManifestSource Source,
    [property: JsonRequired] ManifestProvider Provider,
    [property: JsonRequired] int Dimensions,
    [property: JsonRequired] int ContextWindowTokens,
    [property: JsonRequired] NormalizationMode Normalization,
    string? QueryInstruction,
    [property: JsonRequired] bool RequiresTokenTypeIds,
    [property: JsonRequired] MRLInfo MRL,
    [property: JsonRequired] PoolingManifest Pooling,
    [property: JsonRequired] TokenizerManifest Tokenizer,
    [property: JsonRequired] OnnxManifest Onnx);

/// <summary>Where the model came from: the Hugging Face repo id and the revision that was resolved.</summary>
public sealed record ManifestSource(
    [property: JsonRequired] string Repo,
    [property: JsonRequired] string Revision);

/// <summary>Matryoshka (MRL) truncation capability. Informational; only MRL-trained models may
/// declare it, and <see cref="Supported" /> requires <see cref="MinDimensions" />.</summary>
public sealed record MRLInfo(
    [property: JsonRequired] bool Supported,
    int? MinDimensions);

public sealed record PoolingManifest(
    [property: JsonRequired] PoolingMode Mode,
    [property: JsonRequired] PoolingOutputNames OutputNames);

/// <summary>Graph output names the pooling strategy reads: <see cref="Embedding" /> is the pooled
/// output (model-output mode), <see cref="TokenEmbeddings" /> the token-level output.</summary>
public sealed record PoolingOutputNames(
    [property: JsonRequired] string Embedding,
    [property: JsonRequired] string TokenEmbeddings);

public sealed record TokenizerManifest(
    [property: JsonRequired] TokenizerFamily Family,
    [property: JsonRequired] IReadOnlyList<ManifestFile> Files,
    [property: JsonRequired] TokenizerOptionsManifest Options);

public sealed record TokenizerOptionsManifest(
    [property: JsonRequired] bool AddBeginOfSentence,
    [property: JsonRequired] bool AddEndOfSentence,
    [property: JsonRequired] IReadOnlyDictionary<string, int> SpecialTokens);

/// <summary>A pinned file: repo-relative path + SHA-256 (hex). For LFS files the sha256 is the HF
/// LFS oid captured from the tree API before download (D8); for non-LFS files it is the TOFU pin
/// of the bytes actually downloaded.</summary>
public sealed record ManifestFile(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string Sha256);

public sealed record OnnxManifest(
    [property: JsonRequired] IReadOnlyList<string> Inputs,
    string? EmbeddingOutput,
    string? TokenEmbeddingsOutput,
    [property: JsonRequired] IReadOnlyList<ManifestFile> Files);

/// <summary>
///     Semantics of a model directory with NO manifest.json — the pre-manifest custom-path
///     contract (plan §9): WordPiece from the bundled vocab, 384 dims, mean pooling, L2, 256 ctx,
///     token_type_ids fed. A manifest is the only upgrade path to other families/dims (ops §3.6);
///     these constants document the null-manifest legacy golden case.
/// </summary>
public static class LegacyManifestSemantics
{
    public const TokenizerFamily LegacyTokenizerFamily = TokenizerFamily.BertWordpiece;
    public const int LegacyDimensions = 384;
    public const int LegacyContextWindowTokens = 256;
    public const PoolingMode LegacyPoolingMode = PoolingMode.Mean;
    public const NormalizationMode LegacyNormalization = NormalizationMode.L2;
    public const bool LegacyRequiresTokenTypeIds = true;
}
