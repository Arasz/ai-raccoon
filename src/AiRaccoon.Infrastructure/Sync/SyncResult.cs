namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Outcome of one memory sync run.</summary>
public sealed record SyncResult(int Sent, int Received, int Reindexed);
