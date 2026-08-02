namespace AiRaccoon.Core.Memory;

public sealed record MemoryEntry(string Hash, string Path, string Context, string Value, long CreatedAt);
