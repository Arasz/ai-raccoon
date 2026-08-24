using Microsoft.ML.Tokenizers;

namespace AiRaccoon.Infrastructure.Embedding.Download;

/// <summary>Reads a downloaded sentencepiece .model file's piece-to-id table (issue #417: the
/// derivation source when tokenizer_config.json ships no added_tokens_decoder). Reuses
/// <see cref="SentencePieceTokenizer" />'s own parser — not a second one.</summary>
public interface ISentencePieceVocabularyReader
{
    IReadOnlyDictionary<string, int> Read(string modelPath);
}

public sealed class SentencePieceVocabularyReader : ISentencePieceVocabularyReader
{
    public IReadOnlyDictionary<string, int> Read(string modelPath)
    {
        using var stream = File.OpenRead(modelPath);
        return SentencePieceTokenizer.Create(stream, false, false).Vocabulary;
    }
}
