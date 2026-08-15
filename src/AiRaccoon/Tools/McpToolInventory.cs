using System.Reflection;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tools;

/// <summary>
///     The MCP tool surface, derived by reflection over this assembly's own `[McpServerTool]`
///     methods — the product-side counterpart to `tests/.../RegisteredTools.cs`. Infrastructure
///     (where <c>MetricsReportService</c> lives) cannot see these types, so `memory_performance`
///     resolves this list here and passes it in as data (derive-or-delete-the-list:
///     .ai-badger/invariants/derive-or-delete-the-list.md).
/// </summary>
public static class McpToolInventory
{
    public static IReadOnlyList<string> Names() =>
        [
            .. typeof(McpToolInventory).Assembly.GetTypes()
                .Where(t => t.Namespace == "AiRaccoon.Tools" && t.IsClass && !t.IsAbstract)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
                .Where(attr => attr is not null)
                .Select(attr => attr!.Name!)
                .OrderBy(name => name, StringComparer.Ordinal)
        ];
}
