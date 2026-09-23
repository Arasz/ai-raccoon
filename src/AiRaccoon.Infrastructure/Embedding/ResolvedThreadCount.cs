namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>The resolved ORT intra-op thread count for `embedding.threads`, and whether it came from an explicit setting or the halved-core default.</summary>
internal readonly record struct ResolvedThreadCount(int Threads, string Source);
