namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Per-engine tokenizer contract (docs/work/2026-08-21-arbitrary-embedding-models-plan.md D5/D9):
///     <see cref="CountTokens" /> is the ADR-0036 budget unit — content tokens WITHOUT special tokens;
///     <see cref="EncodeToIds" /> reproduces what the embedder will run (special tokens included,
///     truncation NOT applied); <see cref="SpecialTokenReservation" /> is what the engine adds at
///     embed time (2 for BERT [CLS]/[SEP], 2 for xlm-roberta &lt;s&gt;/&lt;/s&gt;, ...). The instance
///     the embedder uses is the same instance a budget resolver hands to the chunker, so the
///     counter and the embedder always agree (ADR-0036 invariant, preserved by construction).
/// </summary>
public interface IEmbeddingTokenizer
{
    int CountTokens(string text);

    IReadOnlyList<int> EncodeToIds(string text, bool addSpecialTokens);

    int SpecialTokenReservation { get; }
}
