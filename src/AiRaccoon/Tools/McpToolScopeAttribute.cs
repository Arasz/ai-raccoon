namespace AiRaccoon.Tools;

/// <summary>
///     Declares the scope of an MCP tool: which targets it may run against. The scope is
///     compiled into the classification by the generator, and a tool that declares none is a
///     build error, reported by <c>ToolScopeAnalyzer</c> (ADR-0139).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolScopeAttribute : Attribute
{
    /// <summary>Declares the tool's scope. The argument is required: the decision is the point.</summary>
    public McpToolScopeAttribute(ToolScope Scope) => this.Scope = Scope;

    /// <summary>The scope the tool declares.</summary>
    public ToolScope Scope { get; }
}
