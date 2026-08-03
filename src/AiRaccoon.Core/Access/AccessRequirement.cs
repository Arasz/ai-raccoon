namespace AiRaccoon.Core.Access;

/// <summary>How much a tool operation touches the bank; maps to the minimum AccessMode that allows it.</summary>
public enum AccessRequirement
{
    Read,
    Write,
    Destructive
}
