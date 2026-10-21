namespace AiRaccoon.Tools;

/// <summary>
///     Declares whether an MCP tool may run against the reserved <c>shared</c> document target.
///     The scope is compiled into the classification by the generator, so a tool that states no
///     scope is a build error rather than a silent default (ADR-0139).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolScopeAttribute : Attribute
{
    /// <summary>Declares the tool's scope. The argument is required: the decision is the point.</summary>
    public McpToolScopeAttribute(bool RequiresOrdinaryProject) =>
        this.RequiresOrdinaryProject = RequiresOrdinaryProject;

    /// <summary>True when the tool requires an ordinary project and may not use the reserved target.</summary>
    public bool RequiresOrdinaryProject { get; }
}
