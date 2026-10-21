namespace AiRaccoon.Core.Memory.Code;

/// <summary>One code_entries row's full content, as get_entry returns it (mirrors MemoryEntry for get_entry).</summary>
public sealed record CodeEntry(string Hash, string Value, string Path, int LineStart, int LineEnd);
