namespace AiRaccoon.Core.Memory;

/// <summary>
///     A write outcome. <see cref="Stored" /> defaults true for every existing positional caller;
///     a rejected write sets it false and carries <see cref="Reason" /> instead of a real hash (see ADR-0032).
/// </summary>
public sealed record MemoryEntry(
    string Hash,
    string Path,
    string Context,
    string Value,
    long CreatedAt,
    bool Stored = true,
    string? Reason = null);
