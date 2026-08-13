using System.Reflection;
using System.Text.RegularExpressions;
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
        var entries = Entries();

        entries.ShouldNotBeEmpty();

        var duplicates = entries
            .GroupBy(e => e.EventId)
            .Where(g => g.Count() > 1)
            .Select(g => $"EventId {g.Key}: {string.Join(", ", g.Select(e => e.Location))}")
            .ToList();

        duplicates.ShouldBeEmpty(string.Join("; ", duplicates));
    }

    /// <summary>Each owner holds one block of ids; two owners' ranges may not interleave.</summary>
    [Fact]
    public void EventIdBlocks_DoNotInterleaveBetweenOwners()
    {
        var blocks = Entries()
            .GroupBy(e => e.Owner)
            .Select(g => (Owner: g.Key, Min: g.Min(e => e.EventId), Max: g.Max(e => e.EventId)))
            .OrderBy(b => b.Min)
            .ToList();

        var overlaps = blocks
            .SelectMany((a, i) => blocks.Skip(i + 1)
                .Where(b => a.Min <= b.Max && b.Min <= a.Max)
                .Select(b => $"{a.Owner} [{a.Min}-{a.Max}] overlaps {b.Owner} [{b.Min}-{b.Max}]"))
            .ToList();

        overlaps.ShouldBeEmpty(string.Join("; ", overlaps));
    }

    /// <summary>The guard is only as wide as the assembly list, so the list is itself asserted.</summary>
    [Fact]
    public void TheGuard_CoversEveryProductAssembly()
    {
        var names = ProductAssemblies.All().Select(a => a.GetName().Name).ToList();

        names.ShouldContain("AiRaccoon");
        names.ShouldContain("AiRaccoon.Core");
        names.ShouldContain("AiRaccoon.Infrastructure");
    }

    /// <summary>
    ///     The doc calls its table "that measurement, not a hand-maintained list", but nothing held it
    ///     to that — it drifted twice in one day. These two derive it from the assemblies instead.
    /// </summary>
    [Fact]
    public void DocumentedCount_MatchesTheMeasuredCount()
    {
        var documented = DeclaredCount();

        documented.ShouldBe(Entries().Count,
            $"docs/reference/logging-event-ids.md declares {documented} [LoggerMessage] methods; " +
            "re-measure it against src/ and update the prose and the table together");
    }

    [Fact]
    public void EveryEventIdInSource_FallsInsideADocumentedBlock()
    {
        var documented = DocumentedIds();

        var undocumented = Entries()
            .Where(e => !documented.Contains(e.EventId))
            .Select(e => $"{e.EventId} ({e.Location})")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        undocumented.ShouldBeEmpty(
            "these EventIds exist in src/ but fall outside every block in the allocation table: " +
            string.Join(", ", undocumented));
    }

    private static string Registry() => File.ReadAllText(TestData.RepoFile("docs/reference/logging-event-ids.md"));

    /// <summary>The count the doc's prose claims it measured.</summary>
    private static int DeclaredCount()
    {
        var match = Regex.Match(Registry(), @"\*\*(\d+)\*\*\s+`\[LoggerMessage\]`");
        match.Success.ShouldBeTrue("could not find the measured-count sentence in logging-event-ids.md");
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>Every id the allocation table claims, expanding "300, 301" and "500-509" alike.</summary>
    private static HashSet<int> DocumentedIds()
    {
        var ids = new HashSet<int>();
        // Anchored on a digit so the table's own |---|---| separator row is not read as a block.
        foreach (Match row in Regex.Matches(Registry(), @"^\|\s*(\d[\d,\s\-]*?)\s*\|", RegexOptions.Multiline))
        {
            foreach (var part in row.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries |
                                                                StringSplitOptions.RemoveEmptyEntries))
            {
                var bounds = part.Split('-', StringSplitOptions.TrimEntries);
                var first = int.Parse(bounds[0]);
                var last = int.Parse(bounds[^1]);
                for (var id = first; id <= last; id++)
                {
                    ids.Add(id);
                }
            }
        }

        ids.ShouldNotBeEmpty("could not parse any id block out of logging-event-ids.md's table");
        return ids;
    }

    /// <summary>Every [LoggerMessage] in the product assemblies, with the type that owns its id block.</summary>
    private static List<(int EventId, string Owner, string Location)> Entries() =>
    [
        .. ProductAssemblies.All()
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.GetCustomAttributes<LoggerMessageAttribute>()
                .Select(a => (a.EventId, Owner: OwnerOf(m.DeclaringType),
                    Location: $"{m.DeclaringType?.FullName ?? "<unknown>"}.{m.Name}")))
    ];

    /// <summary>The outermost type — a nested `Log` class belongs to the id block of the class hosting it.</summary>
    private static string OwnerOf(Type? declaringType)
    {
        var type = declaringType;
        while (type?.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type?.FullName ?? "<unknown>";
    }
}
