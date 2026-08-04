namespace AiRaccoon.Core.Access;

/// <summary>Access modes (FR-NM-2): ro is read-only, rw adds writes, full adds removal and forgetting knobs.</summary>
public enum AccessMode
{
    Ro,
    Rw,
    Full
}
