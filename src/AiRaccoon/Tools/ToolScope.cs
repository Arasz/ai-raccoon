namespace AiRaccoon.Tools;

/// <summary>
///     The scope a tool declares: which targets it may run against. The reserved
///     <c>shared</c> document target's scope is <see cref="Shared" />; an ordinary project's is
///     <see cref="Project" />. A tool's scope must cover the target's scope, so a tool
///     declared <see cref="All" /> runs against either, one declared <see cref="Project" />
///     needs an ordinary project, and one declared <see cref="Shared" /> needs the reserved
///     target (ADR-0139).
/// </summary>
public enum ToolScope
{
    /// <summary>Any target: an ordinary project or the reserved shared target.</summary>
    All,

    /// <summary>The reserved shared target only.</summary>
    Shared,

    /// <summary>An ordinary project only.</summary>
    Project
}
