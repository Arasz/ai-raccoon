using System.Reflection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     [LoggerMessage] EventIds must be unique across every AiRaccoon* assembly — no exceptions,
///     and no hardcoded assembly list to fall out of date when a project gains its first
///     [LoggerMessage] method. Ownership of each block is recorded in
///     docs/reference/logging-event-ids.md.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class LoggerMessageEventIdTests
{
    [Fact]
    public void EventIds_AreUniqueAcrossTheAssemblies()
    {
        var entries = DiscoverAiRaccoonAssemblies()
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

    /// <summary>The discovery itself must not silently find zero assemblies and pass vacuously.</summary>
    [Fact]
    public void DiscoverAiRaccoonAssemblies_FindsAllThreeProductionAssembliesByName()
    {
        var names = DiscoverAiRaccoonAssemblies().Select(a => a.GetName().Name).ToList();

        names.ShouldNotBeEmpty();
        names.ShouldContain("AiRaccoon");
        names.ShouldContain("AiRaccoon.Infrastructure");
        names.ShouldContain("AiRaccoon.Core");
    }

    /// <summary>
    ///     Force-loads every AiRaccoon* assembly the test project references (directly or
    ///     transitively) so a production project not yet touched by another test still shows up,
    ///     then returns whatever AiRaccoon* (non-test) assemblies are loaded. No hardcoded list.
    /// </summary>
    private static IReadOnlyList<Assembly> DiscoverAiRaccoonAssemblies()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void LoadTransitively(Assembly assembly)
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name is not { } name || !name.StartsWith("AiRaccoon", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen.Add(reference.FullName))
                {
                    LoadTransitively(Assembly.Load(reference));
                }
            }
        }

        LoadTransitively(Assembly.GetExecutingAssembly());

        return [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } name &&
                        name.StartsWith("AiRaccoon", StringComparison.Ordinal) &&
                        !name.EndsWith(".Tests", StringComparison.Ordinal))];
    }
}
