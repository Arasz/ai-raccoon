using System.Reflection;
using AiRaccoon.Infrastructure.Promotion;
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
        Assembly[] assemblies =
        [
            typeof(MemoryTools).Assembly,
            typeof(PromotionQueueService).Assembly
        ];

        var entries = assemblies
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
}
