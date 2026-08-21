namespace AiRaccoon.Core.Memory.Code;

/// <summary>One code_entries row's full content, as code_get returns it (mirrors MemoryEntry for memory_get).</summary>
public sealed record CodeEntry(string Hash, string Value, string Path, int LineStart, int LineEnd);
