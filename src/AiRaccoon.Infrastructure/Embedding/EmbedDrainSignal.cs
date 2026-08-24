using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>The one place a written <see cref="CorpusKind" /> becomes an embed-topic signal.</summary>
public static class EmbedDrainSignal
{
    /// <summary>Signals the drain for the corpus a write landed in; <see cref="CorpusKind.Neither" /> signals nothing.</summary>
    public static void SignalWritten(this IEventPump<EmbedDrainRequest> pump, CorpusKind written)
    {
        var corpus = written switch
        {
            CorpusKind.Memory => EmbedCorpus.Memory,
            CorpusKind.Code => EmbedCorpus.Code,
            CorpusKind.Neither => (EmbedCorpus?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(written), written, null)
        };
        if (corpus is { } c)
        {
            pump.TryEnqueue(new EmbedDrainRequest(c));
        }
    }
}
