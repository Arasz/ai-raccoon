namespace AiRaccoon.Core.Memory;

/// <summary>The requested note scope; custom contexts stay within Project.</summary>
public enum MemoryWriteScope
{
    Project,
    Workspace,
    Shared
}
