namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Counts code tokens the way the bundled engine (ADR-0108) embeds them, for <c>CodeChunker</c>
///     when no other code engine is configured. Built lazily from the bundled tokenizer.json, so a
///     broken install fails at first use rather than at container build.
/// </summary>
public sealed class CodeTokenizer : ICodeTokenizer
{
    private readonly Lazy<IEmbeddingTokenizer> _tokenizer;

    public CodeTokenizer()
        : this(() => TokenizerJsonEmbeddingTokenizer.Create(Path.Combine(BundledModel.ResolveDirectory(), "tokenizer.json")))
    {
    }

    internal CodeTokenizer(Func<IEmbeddingTokenizer> factory)
    {
        _tokenizer = new Lazy<IEmbeddingTokenizer>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal bool IsTokenizerBuilt => _tokenizer.IsValueCreated;

    public int CountTokens(string text) => _tokenizer.Value.CountTokens(text);
}
