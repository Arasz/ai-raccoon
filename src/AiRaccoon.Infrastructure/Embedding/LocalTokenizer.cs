using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Shared BERT WordPiece tokenizer for the bundled local embedding engine (docs/adr/0036): built
///     lazily from the bundled vocab.txt on first use and reused for every call after that, instead
///     of re-parsing the ~30k-entry vocab per call site. The bundled vocab never changes with the
///     configured model path (<see cref="Embedding.BundledModel.ResolveVocabPath()" /> takes no
///     parameter), so nothing here needs invalidating.
/// </summary>
/// <remarks>
///     Lazy, not eager: a broken install (missing/replaced bundled asset) must keep failing at first
///     *use*, not at container-build time — <see cref="Lazy{T}" /> also caches the thrown exception,
///     which is correct here. Thread safety is safe-by-inspection, not a documented package contract:
///     Microsoft.ML.Tokenizers 2.0.0's BertTokenizer/WordPieceTokenizer hold only private readonly
///     vocab state and allocate per-call locals (ArrayPool-rented, not instance buffers) in
///     CountTokens/EncodeToIds. Re-check this on a package version bump (Directory.Packages.props).
/// </remarks>
public sealed class LocalTokenizer : ILocalTokenizer
{
    private readonly Lazy<BertTokenizer> _tokenizer;

    public LocalTokenizer()
        : this(() => OnnxEmbeddingGenerator.CreateTokenizer(BundledModel.ResolveVocabPath()))
    {
    }

    internal LocalTokenizer(Func<BertTokenizer> factory)
    {
        _tokenizer = new Lazy<BertTokenizer>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Testability seam: whether the underlying BertTokenizer has been built yet.</summary>
    internal bool IsTokenizerBuilt => _tokenizer.IsValueCreated;

    public int CountTokens(string text) => _tokenizer.Value.CountTokens(text);
}
