using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit;

/// <summary>
///     Enforces the "pin actions to a commit SHA" invariant (.ai-badger/invariants/pin-actions-to-sha.md):
///     every action a workflow uses is referenced by a full 40-hex commit, never a tag or branch.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WorkflowActionPinTests
{
    private static readonly Regex UsesLine = new(@"^\s*(?:-\s+)?uses:\s*""?(?<ref>[^\s""]+)", RegexOptions.Compiled);

    private static readonly Regex PinnedToSha = new(@"^[^@]+@[0-9a-f]{40}$", RegexOptions.Compiled);

    [Fact]
    public void EveryWorkflowAction_IsPinnedToACommitSha()
    {
        var offenders = ActionRefs()
            .Where(r => !r.Ref.StartsWith("./", StringComparison.Ordinal))
            .Where(r => !PinnedToSha.IsMatch(r.Ref))
            .Select(r => $"{r.File}:{r.Line}: {r.Ref}")
            .ToList();

        offenders.ShouldBeEmpty(
            "these actions are referenced by a mutable tag or branch instead of a commit SHA: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void TheGuardSeesTheWorkflowActions()
    {
        ActionRefs().Count.ShouldBeGreaterThan(5);
    }

    private static List<(string File, int Line, string Ref)> ActionRefs()
    {
        var workflows = Path.Combine(FindRepoRoot(), ".github", "workflows");
        return
        [
            .. Directory.EnumerateFiles(workflows, "*.y*ml")
                .SelectMany(file => File.ReadLines(file)
                    .Select((text, i) => (Match: UsesLine.Match(text), Line: i + 1))
                    .Where(x => x.Match.Success)
                    .Select(x => (Path.GetFileName(file), x.Line, x.Match.Groups["ref"].Value)))
        ];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find repo root (AiRaccoon.slnx) walking up from " + AppContext.BaseDirectory);
    }
}
