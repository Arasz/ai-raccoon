using System.Reflection;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     [LoggerMessage] EventIds must be unique across the assemblies — no exceptions. Ownership
///     of each block is recorded in docs/reference/logging-event-ids.md.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class LoggerMessageEventIdTests
{
    [Fact]
    public void EventIds_AreUniqueAcrossTheAssemblies()
    {
        var entries = ProductAssemblies()
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes<LoggerMessageAttribute>()
                .Select(a => (a.EventId, Location: $"{m.DeclaringType?.FullName ?? "<unknown>"}.{m.Name}")))
            .ToList();

        entries.ShouldNotBeEmpty();

        var duplicates = entries
            .GroupBy(e => e.EventId)
            .Where(g => g.Count() > 1)
            .Select(g => $"EventId {g.Key}: {string.Join(", ", g.Select(e => e.Location))}")
            .ToList();

        duplicates.ShouldBeEmpty(string.Join("; ", duplicates));
    }

    /// <summary>The guard is only as wide as the assembly list, so the list is itself asserted (#119 item 2).</summary>
    [Fact]
    public void TheGuard_CoversEveryProductAssembly()
    {
        var names = ProductAssemblies().Select(a => a.GetName().Name).ToList();

        names.ShouldContain("AiRaccoon");
        names.ShouldContain("AiRaccoon.Core");
        names.ShouldContain("AiRaccoon.Infrastructure");
    }

    /// <summary>Walks the reference graph rather than a hardcoded array: a new AiRaccoon assembly joins the guard by existing.</summary>
    private static List<Assembly> ProductAssemblies()
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
