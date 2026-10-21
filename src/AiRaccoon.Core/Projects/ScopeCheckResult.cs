namespace AiRaccoon.Core.Projects;

/// <summary>A scope decision; a null error allows the operation.</summary>
public sealed record ScopeCheckResult(string? Error)
{
    /// <summary>True when the operation has no scope refusal.</summary>
    public bool IsAllowed => Error is null;
}
