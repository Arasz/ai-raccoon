using System.Reflection;
using AiRaccoon.Tools;
using ModelContextProtocol.Server;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     The MCP tool surface, derived from the product assembly at test time. One source, so a test
///     asserting "the server exposes the right tools" cannot drift from what the server exposes
///     (.ai-badger/invariants/derive-or-delete-the-list.md).
/// </summary>
public static class RegisteredTools
{
    /// <summary>Every `[McpServerTool]` method declared in the `AiRaccoon.Tools` namespace.</summary>
    public static IEnumerable<(Type Class, MethodInfo Method, McpServerToolAttribute Attr)> Methods()
    {
        foreach (var type in typeof(MemoryTools).Assembly.GetTypes()
                     .Where(t => t.Namespace == "AiRaccoon.Tools" && t.IsClass && !t.IsAbstract))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    yield return (type, method, attr);
                }
            }
        }
    }

    /// <summary>
    ///     Tool names, ordinal-sorted so a caller can compare against a sorted actual set. No
    ///     fallback to the method name: <see cref="AiRaccoon.Tests.Unit.Mcp.McpToolInventoryTests.AllTools_DeclareAnExplicitName" />
    ///     (F10) guarantees every attribute's <c>Name</c> is explicit, so the ambiguous case cannot exist.
    /// </summary>
    public static IReadOnlyList<string> Names() =>
    [
        .. Methods()
            .Select(x => x.Attr.Name!)
            .OrderBy(name => name, StringComparer.Ordinal)
    ];

    /// <summary>How many tools the product declares. Never write this number into a test.</summary>
    public static int Count => Methods().Count();
}
