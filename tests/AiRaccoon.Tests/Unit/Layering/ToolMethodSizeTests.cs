using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Layering;

/// <summary>
///     "MCP stays thin" (.ai-badger/invariants/mcp-thin.md) had no mechanical check, so
///     `memory_share_extract` grew to 62 body lines against a median of 9 — a consent gate, a mode
///     decision and two orchestration pipelines that exist nowhere else, unreachable from the CLI and
///     the background loop and impossible to unit-test (WP8, docs/adr/0065).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed partial class ToolMethodSizeTests
{
    /// <summary>
    ///     Body lines allowed in a tool method. A thin tool validates nothing of its own, calls one
    ///     service and wraps the envelope.
    ///     <para>
    ///         Raise history — raising this is how the layer gets fat again, so it needs a reason:
    ///         <list type="bullet">
    ///             <item>
    ///                 40 — measured 2026-08-15 after WP8 extracted ShareExtractService and
    ///                 QueryGuardService. The cap exists to keep a POLICY ENGINE out of the tool
    ///                 layer, and after WP8 none is left: the largest remaining method,
    ///                 MemoryTools.Search at 39, is a SearchQuery construction plus telemetry and
    ///                 shadow reporting — nothing another caller needs. It could have been pushed
    ///                 under 30 by moving those lines into a private method of the same class, but
    ///                 the gate measures per method, so that would move the number without moving
    ///                 the logic. Set where the code honestly lands. It still catches what it was
    ///                 built for: ShareExtract at 51.
    ///             </item>
    ///         </list>
    ///     </para>
    /// </summary>
    private const int MaxBodyLines = 40;

    /// <summary>Anchored on a file rather than the folder — TestData.RepoFile resolves files.</summary>
    private static string ToolsDirectory =>
        Path.GetDirectoryName(TestData.RepoFile("src/AiRaccoon/Tools/ShareTools.cs"))!;

    [GeneratedRegex(@"\[McpServerTool[^\]]*\]", RegexOptions.None)]
    private static partial Regex ToolAttribute();

    [Fact]
    public void EveryToolMethod_IsThin()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(ToolsDirectory, "*.cs"))
        {
            foreach (var (name, lines) in ToolMethodBodies(File.ReadAllLines(file)))
            {
                if (lines > MaxBodyLines)
                {
                    offenders.Add($"{Path.GetFileName(file)}::{name} = {lines} lines");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"a tool maps onto the service behind it and holds no logic of its own (cap {MaxBodyLines}): "
            + string.Join("; ", offenders));
    }

    /// <summary>The scan must see the tools; an empty sweep passes for the same reason a broken one does.</summary>
    [Fact]
    public void EveryToolMethod_IsThin_ScansTheWholeToolSurface()
    {
        var found = Directory.EnumerateFiles(ToolsDirectory, "*.cs")
            .SelectMany(file => ToolMethodBodies(File.ReadAllLines(file)))
            .Count();

        found.ShouldBeGreaterThanOrEqualTo(26, "the bank exposes 26 tools; the scan must reach all of them");
    }

    /// <summary>
    ///     Counts the braces-balanced body of each `[McpServerTool]` method. Blank lines and lines
    ///     that are only a comment do not count — the cap is about logic, not about explaining it.
    /// </summary>
    private static IEnumerable<(string Name, int Lines)> ToolMethodBodies(string[] source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            if (!ToolAttribute().IsMatch(source[i]))
            {
                continue;
            }

            var open = Array.FindIndex(source, i, line => line.TrimEnd().EndsWith('{'));
            if (open < 0)
            {
                continue;
            }

            var name = MethodNameNear(source, i, open);
            var depth = 0;
            var body = 0;
            for (var j = open; j < source.Length; j++)
            {
                depth += source[j].Count(c => c == '{') - source[j].Count(c => c == '}');
                if (j > open && !IsNoise(source[j]))
                {
                    body++;
                }

                if (depth == 0)
                {
                    yield return (name, body - 1);
                    i = j;
                    break;
                }
            }
        }
    }

    private static bool IsNoise(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    private static string MethodNameNear(string[] source, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            var match = Regex.Match(source[i], @"public\s+(?:async\s+)?[\w<>?,\s\[\]]+\s+(\w+)\s*\(");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return "<unknown>";
    }
}
