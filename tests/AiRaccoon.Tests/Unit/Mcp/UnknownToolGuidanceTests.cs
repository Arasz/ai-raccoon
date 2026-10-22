using System.Text.RegularExpressions;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The guidance a caller gets for a tool name this server does not have: the name to call
///     instead when the name was renamed in 2.0.0, and the general unknown-tool message otherwise.
///     The rename map is a hand-written list of facts about a past release, so it is guarded from
///     both directions — a name that came back onto the surface (an alias crept in) and a target
///     that left it (a later rename) both fail here, and the map is compared with the changelog
///     table that records the same rename.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class UnknownToolGuidanceTests
{
    [Fact]
    public void Message_ForARenamedName_NamesTheToolToCall()
    {
        var message = UnknownToolGuidance.Message("memory_search");

        message.ShouldStartWith($"{ToolRefusals.UnknownToolPrefix}:");
        message.ShouldContain("'query'");
    }

    [Fact]
    public void Message_ForAnUnknownName_SaysThereIsNoSuchTool()
    {
        var message = UnknownToolGuidance.Message("no_such_tool");

        message.ShouldStartWith($"{ToolRefusals.UnknownToolPrefix}:");
        message.ShouldContain("no_such_tool");
        message.ShouldContain("tools/list");
    }

    /// <summary>The renamed names are gone from the surface — 2.0.0 shipped no aliases.</summary>
    [Fact]
    public void RenamedNames_AreNotOnTheCurrentSurface()
    {
        var surface = McpToolInventory.Names().ToHashSet(StringComparer.Ordinal);

        var stillThere = RenamedToolNames.Map.Keys.Where(surface.Contains).Order(StringComparer.Ordinal).ToList();

        stillThere.ShouldBeEmpty(
            $"these renamed tools are back on the surface: {string.Join(", ", stillThere)}");
    }

    /// <summary>
    ///     Every name the map points at is a tool this server has — a target that left the surface
    ///     would send the caller to a tool that does not exist.
    /// </summary>
    [Fact]
    public void RenameTargets_AreOnTheCurrentSurface()
    {
        var surface = McpToolInventory.Names().ToHashSet(StringComparer.Ordinal);

        var missing = RenamedToolNames.Map.Values.Distinct(StringComparer.Ordinal)
            .Where(name => !surface.Contains(name)).Order(StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            $"the rename map points at tools this server does not have: {string.Join(", ", missing)}");
    }

    /// <summary>
    ///     The map and the changelog table that records the 2.0.0 rename are the same fact written
    ///     twice; this is what keeps them the same.
    /// </summary>
    [Fact]
    public void RenameMap_MatchesTheChangelogTable()
    {
        var changelog = File.ReadAllText(
            TestData.RepoFile("docs/changelog/2.0.0-mcp-tool-surface-renamed.md"));

        var expected = ParseRenameTable(changelog);

        expected.ShouldNotBeEmpty("the changelog's rename table matched nothing — has its format changed?");

        var actual = RenamedToolNames.Map.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();

        missing.ShouldBeEmpty(
            $"the 2.0.0 changelog renames these tools, the map does not: {Format(missing)}");
        extra.ShouldBeEmpty(
            $"the map renames tools the 2.0.0 changelog does not record: {Format(extra)}");
    }

    private static string Format(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join(", ", pairs.Select(pair => $"'{pair.Key}'->'{pair.Value}'"));

    /// <summary>
    ///     Reads the changelog's old-to-new table. A row pairs its names positionally
    ///     (<c>memory_ingest_file</c>, <c>memory_ingest_directory</c> against <c>ingest_file</c>,
    ///     <c>ingest_directory</c>); one new name for several old ones maps all of them onto it
    ///     (<c>memory_get</c> and <c>code_get</c> both became <c>get_entry</c>); a leading
    ///     underscore continues the previous name's prefix (<c>_status</c> after
    ///     <c>memory_workspace_begin</c> is <c>memory_workspace_status</c>).
    /// </summary>
    private static Dictionary<string, string> ParseRenameTable(string changelog)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match row in Regex.Matches(changelog, @"^\|(.+)\|$", RegexOptions.Multiline))
        {
            var cells = row.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length != 2)
            {
                continue;
            }

            var oldNames = Names(cells[0]);
            var newNames = Names(cells[1]);
            if (oldNames.Count == 0 || newNames.Count == 0)
            {
                continue;
            }

            if (newNames.Count == 1)
            {
                foreach (var oldName in oldNames)
                {
                    map[oldName] = newNames[0];
                }
            }
            else
            {
                for (var index = 0; index < oldNames.Count && index < newNames.Count; index++)
                {
                    map[oldNames[index]] = newNames[index];
                }
            }
        }

        return map;
    }

    /// <summary>The backticked names in one table cell, with the leading-underscore shorthand expanded.</summary>
    private static List<string> Names(string cell)
    {
        var names = new List<string>();
        foreach (Match name in Regex.Matches(cell, @"`([^`]+)`"))
        {
            var value = name.Groups[1].Value;
            if (value.StartsWith('_') && names.Count > 0)
            {
                var previous = names[^1];
                var lastUnderscore = previous.LastIndexOf('_');
                value = lastUnderscore < 0 ? value : string.Concat(previous[..(lastUnderscore + 1)], value[1..]);
            }

            names.Add(value);
        }

        return names;
    }
}
