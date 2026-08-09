using System.Reflection;
using AiRaccoon.Tools;

namespace AiRaccoon.Tests;

/// <summary>The AiRaccoon assemblies, walked from the reference graph rather than a hardcoded
/// array: a new AiRaccoon assembly joins every guard built on this by existing.</summary>
public static class ProductAssemblies
{
    public static IReadOnlyList<Assembly> All()
    {
        var found = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>([typeof(MemoryTools).Assembly]);
        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            var name = assembly.GetName().Name;
            if (name is null || !name.StartsWith("AiRaccoon", StringComparison.Ordinal) || !found.TryAdd(name, assembly))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies()
                         .Where(r => r.Name?.StartsWith("AiRaccoon", StringComparison.Ordinal) == true))
            {
                pending.Enqueue(Assembly.Load(reference));
            }
        }

        return [.. found.Values];
    }
}
