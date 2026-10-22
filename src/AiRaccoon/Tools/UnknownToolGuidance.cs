namespace AiRaccoon.Tools;

/// <summary>
///     What the server says to a caller that named a tool it does not have. A renamed name gets
///     the name to call instead — the 2.0.0 rename shipped with no aliases, so a client still
///     speaking the old names can only be helped by being told; any other name gets the general
///     unknown-tool message. The tool surface itself is the source of truth for what exists
///     (<see cref="McpToolInventory" />, generated from the <c>[McpServerTool]</c> declarations).
/// </summary>
internal static class UnknownToolGuidance
{
    private static readonly HashSet<string> KnownTools = new(McpToolInventory.Names(), StringComparer.Ordinal);

    /// <summary>Whether this server has a tool with that name.</summary>
    internal static bool IsKnownTool(string? name) => name is not null && KnownTools.Contains(name);

    /// <summary>
    ///     The wire text for a call that named a tool this server does not have: the current name
    ///     of a renamed tool, or the general message.
    /// </summary>
    internal static string Message(string name) =>
        RenamedToolNames.Map.TryGetValue(name, out var current)
            ? $"{ToolRefusals.UnknownToolPrefix}: '{name}' was renamed to '{current}' in {RenamedToolNames.RenamedInVersion} — call '{current}' instead."
            : $"{ToolRefusals.UnknownToolPrefix}: '{name}' is not a tool on this server. This server exposes {McpToolInventory.Names().Count} tools; call tools/list to see them.";

    /// <summary>What the log record says — the same fact, without repeating the wire text.</summary>
    internal static string Reason(string name) =>
        RenamedToolNames.Map.TryGetValue(name, out var current)
            ? $"'{name}' was renamed to '{current}' in {RenamedToolNames.RenamedInVersion}"
            : $"'{name}' is not a tool on this server";
}
