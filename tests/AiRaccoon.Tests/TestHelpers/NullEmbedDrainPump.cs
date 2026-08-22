using AiRaccoon.Core.EventPump;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>Test double for a test that constructs <see cref="AiRaccoon.Infrastructure.Ingestion.FileIngestor" />
/// or <see cref="AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore" /> without caring about
/// the embed topic — every enqueue reports "dropped", never queues, and nothing ever drains it.
/// Production always supplies the real shared pump; this has no production use.</summary>
public sealed class NullEmbedDrainPump : IEventPump<EmbedDrainRequest>
{
    public static NullEmbedDrainPump Instance { get; } = new();

    public long EnqueuedCount => 0;

    public long DroppedCount => 0;

    public long CoalescedCount => 0;

    public bool TryEnqueue(EmbedDrainRequest item) => false;

    public IReadOnlyList<EmbedDrainRequest> DrainUpTo(int budget) => [];

    public Task WaitForItemAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

    public void ApplyCapacity(int capacity)
    {
    }
}
