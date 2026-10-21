namespace AiRaccoon.Tools;

/// <summary>The scope each MCP tool declares.</summary>
public interface IToolNameScope
{
    /// <summary>
    ///     The scope the named tool declares. A name that is not a declared tool is
    ///     <see cref="ToolScope.Project" />: the map knows every tool, so a name outside it names
    ///     no tool at all, and the default is deny (ADR-0139).
    /// </summary>
    ToolScope ScopeOf(string toolName);
}
