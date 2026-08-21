using AiRaccoon.Infrastructure.Embedding.Manifest;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     PROVISIONAL engine descriptor — lane B's WP3 adapter over the D1 manifest JSON shape
///     (docs/work/2026-08-21-arbitrary-embedding-models-plan.md D1/D5; architecture doc §5.6).
///     Lane A (WP1) builds the canonical <c>EmbeddingModelDescriptor</c> record + serializer in
///     parallel; at the join this adapter and <see cref="ProvisionalManifestDescriptor" /> are
///     replaced by it. Do not build new features on this type's surface beyond the WP3 gate.
/// </summary>
public sealed record EngineDescriptor(
    string Model,
    string? SourceRepo,
    string? SourceRevision,
    int Dimensions,
    int ContextWindowTokens,
    string Normalization,
    string Pooling,
    int SpecialTokenReservation,
    string TokenizerFamily,
    string TokenizerFile,
    SentencePieceTokenizerOptions? SentencePieceOptions,
    bool RequiresTokenTypeIds,
    IReadOnlyList<string> InputNames,
    string TokenEmbeddingsOutput,
    string? EmbeddingOutput,
    string OnnxModelFile,
    IReadOnlyList<ManifestFile> Files)
{
    /// <summary>Both supported families reserve exactly two special tokens at embed time (D6).</summary>
    public const int DefaultSpecialTokenReservation = 2;
}
