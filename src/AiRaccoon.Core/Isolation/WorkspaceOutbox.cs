using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Isolation;

/// <summary>A workspace record and the entries currently sitting in its outbox.</summary>
public sealed record WorkspaceOutbox(Workspace Workspace, IReadOnlyList<MemoryEntry> Entries);
