using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     "Which rows belong to this project" is defined once, in ProjectRows (ADR-0046). It was
///     previously hand-copied as `scope = 'project'` into nine queries, a trigger and a filter, each
///     carrying a comment naming another one as the authority — and when ADR-0045 moved the rule,
///     six of them silently disagreed. A hand-maintained mirror needs something that compares the
///     copies (.ai-badger/invariants/derive-or-delete-the-list.md); this is it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ProjectRowsSingleDefinitionTests
{
    /// <summary>
    ///     The two legitimate holdouts, each with the reason it is not a project-membership test:
    ///     the context-key CASE maps a row to its display context, and the per-context search filter
    ///     builds one context's predicate — the custom contexts get their own query, so folding them
    ///     together would make every context search every context.
    /// </summary>
    private static readonly string[] Allowed =
    [
        "WHEN {prefix}scope = 'project' THEN 'project:' || {prefix}project_id",
        "$\"{alias}scope = 'project' AND {alias}project_id = @projectId\"",
        // Display ordering, not membership: puts shared first, then the project's own rows.
        "ORDER BY CASE WHEN scope = 'shared' THEN 0 WHEN scope = 'project' THEN 1 ELSE 2 END"
    ];

    [Fact]
    public void NoSourceFileHandRollsTheProjectScopePredicate()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "ProjectRows.cs")
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!Regex.IsMatch(line, @"scope\s*=\s*'project'"))
                {
                    continue;
                }

                // Prose mentions the old literal when explaining why it went away. A gate that
                // flags its own rationale gets suppressed rather than obeyed.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (Allowed.Any(a => line.Contains(a, StringComparison.Ordinal)))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        offenders.ShouldBeEmpty(
            "a project-membership test must go through ProjectRows, not a copied literal — " +
            $"found:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate src/ from the test output directory.");
    }
}
