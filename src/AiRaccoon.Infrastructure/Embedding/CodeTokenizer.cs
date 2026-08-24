namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Shared sentencepiece tokenizer for the bundled code-daemon-embed-v1 counting path
///     (docs/work/2026-08-21-code-search-implementation-plan.md §3.3/§3.4, OQ3 approved): built
///     lazily from the bundled <see cref="ModelFileName" /> on first use and reused for every call
///     after that. Used by <c>CodeChunker</c> whenever no code engine is configured
///     (embedding.codeModel absent) — the v1 default; a configured engine's own tokenizer (WP5)
///     replaces this path.
/// </summary>
/// <remarks>
///     Lazy, not eager, for the same reason as <see cref="LocalTokenizer" />: a broken install
///     must keep failing at first *use*, not at container-build time.
/// </remarks>
public sealed class CodeTokenizer : ICodeTokenizer
{
    public const string ModelFileName = "code-sentencepiece.bpe.model";
    public const string ModelSha256 = "3236c10b708765fdfd0720ea6ea932e1472450cd927b331107f05bfacdba7549";

    // code-daemon-embed-v1's own sentencepiece vocab ids for <s>/</s>/<pad>/<unk> (verified by
    // spike, plan §1 / contracts "Manifest fixture shape") — NOT the xlm-roberta HF wrapper's
    // config.json ids, which differ (same distinction SentencePieceEmbeddingTokenizerTests.cs
    // documents for bge-m3's own bos/eos ids).
    private static readonly IReadOnlyDictionary<string, int> SpecialTokens =
        new Dictionary<string, int> { ["<s>"] = 2, ["</s>"] = 3, ["<pad>"] = 0, ["<unk>"] = 1 };

    private readonly Lazy<SentencePieceEmbeddingTokenizer> _tokenizer;

    public CodeTokenizer()
        : this(() => SentencePieceEmbeddingTokenizer.Create(ResolveModelPath(),
            new SentencePieceTokenizerOptions(SpecialTokens: SpecialTokens)))
    {
    }

    internal CodeTokenizer(Func<SentencePieceEmbeddingTokenizer> factory)
    {
        _tokenizer = new Lazy<SentencePieceEmbeddingTokenizer>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Testability seam: whether the underlying tokenizer has been built yet.</summary>
    internal bool IsTokenizerBuilt => _tokenizer.IsValueCreated;

    public int CountTokens(string text) => _tokenizer.Value.CountTokens(text);

    /// <summary>The bundled code tokenizer file next to the running tool, or the repo source copy during tests.</summary>
    public static string ResolveModelPath() => ResolveModelPath(AppContext.BaseDirectory);

    internal static string ResolveModelPath(string baseDirectory) =>
        BundledModel.ResolveBundled(ModelFileName, baseDirectory)
        ?? throw new InvalidOperationException(
            $"Bundled code tokenizer '{ModelFileName}' not found next to the tool. Reinstall the tool to restore it.");
}
