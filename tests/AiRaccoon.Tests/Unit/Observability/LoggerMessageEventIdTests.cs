using System.Reflection;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     [LoggerMessage] EventIds must be unique per Log class, except the deferred 1/2/3 reuse
///     tracked in docs/reference/logging-event-ids.md (cross-cutting renumber, out of this task's
///     lane). Any other duplicate is a regression this test catches.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class LoggerMessageEventIdTests
{
    private static readonly HashSet<int> DeferredDuplicateEventIds = [1, 2, 3];

    [Fact]
    public void EventIds_AreUniqueAcrossTheAssemblies_ExceptTheDeferredIds()
    {
        Assembly[] assemblies =
        [
            typeof(MemoryTools).Assembly,
            typeof(PromotionQueueService).Assembly
        ];

        var entries = assemblies
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Attribute!.EventId, Location: $"{x.Method.DeclaringType!.FullName}.{x.Method.Name}"))
            .ToList();

        entries.ShouldNotBeEmpty();

        var duplicates = entries
            .GroupBy(e => e.EventId)
            .Where(g => g.Count() > 1 && !DeferredDuplicateEventIds.Contains(g.Key))
            .Select(g => $"EventId {g.Key}: {string.Join(", ", g.Select(e => e.Location))}")
            .ToList();

        duplicates.ShouldBeEmpty(string.Join("; ", duplicates));
    }
}
