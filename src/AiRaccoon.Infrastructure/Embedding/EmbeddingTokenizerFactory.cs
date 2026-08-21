namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Builds the <see cref="IEmbeddingTokenizer" /> a manifest descriptor selects (D5/D9).</summary>
public interface ITokenizerFactory
{
    IEmbeddingTokenizer Create(EngineDescriptor descriptor, string modelDirectory);
}

/// <inheritdoc cref="ITokenizerFactory" />
public sealed class EmbeddingTokenizerFactory : ITokenizerFactory
{
    public IEmbeddingTokenizer Create(EngineDescriptor descriptor, string modelDirectory) =>
        descriptor.TokenizerFamily switch
        {
            "bert-wordpiece" => WordPieceEmbeddingTokenizer.Create(Path.Combine(modelDirectory, descriptor.TokenizerFile)),
            "sentencepiece" => SentencePieceEmbeddingTokenizer.Create(
                Path.Combine(modelDirectory, descriptor.TokenizerFile),
                descriptor.SentencePieceOptions
                ?? throw new InvalidOperationException(
                    $"Manifest for '{descriptor.Model}' declares the sentencepiece family without tokenizer.options.")),
            var family => throw new InvalidOperationException(
                $"Tokenizer family '{family}' is not supported (manifest model '{descriptor.Model}').")
        };
}
