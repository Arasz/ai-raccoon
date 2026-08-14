using System.Reflection;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     WP8-ratchet (docs/plans/2026-08-14-code-quality-improvement-plan.md): <c>SqliteMemoryStore</c>
///     grew 1111 -> 1250 lines in eight days of ordinary feature work while its decomposition
///     (WP8, deferred to Wave 5) stayed open. This is the brake in the meantime — it does not fix
///     the god class, it stops it from growing further unnoticed. The caps below are measured
///     directly against this branch (not copied from the review document, which is stale after
///     Wave 1's deletions and the chunking merge): 1245 lines, 25 <see cref="IMemoryStore" /> members.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SqliteMemoryStoreSizeRatchetTests
{
    // Measured on work/wave2c-gates at commit 4cedd158 (2026-08-14): wc -l SqliteMemoryStore.cs
    // and grep -cE "^\s+Task" IMemoryStore.cs. WP8 is the fix; raise these only alongside a
    // conscious decision to accept more growth, never to make a failing build pass.
    // Re-pinned 2026-08-14 from 1245/25 to 1276/26 for memory_get (ADR-0035), which closed
    // blocker B2 -- an agent could find a memory and had no call returning its content. The
    // ratchet did its job here: it forced this to be a decision rather than drift. Raise these
    // only for a reviewed addition, and record it on this line; the alternative is WP8.
    private const int MaxLines = 1276;
    private const int MaxMembers = 26;

    [Fact]
    public void SqliteMemoryStore_DoesNotExceedItsMeasuredLineCap()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "AiRaccoon.Infrastructure", "Sqlite", "SqliteMemoryStore.cs");
        var lines = File.ReadAllLines(path).Length;

        lines.ShouldBeLessThanOrEqualTo(MaxLines,
            $"SqliteMemoryStore.cs is now {lines} lines (cap {MaxLines}, measured on work/wave2c-gates). " +
            "WP8 (docs/plans/2026-08-14-code-quality-improvement-plan.md) is the decomposition — split it, don't raise the cap.");
    }

    [Fact]
    public void IMemoryStore_DoesNotExceedItsMeasuredMemberCap()
    {
        var members = typeof(IMemoryStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Length;

        members.ShouldBeLessThanOrEqualTo(MaxMembers,
            $"IMemoryStore is now a {members}-member port (cap {MaxMembers}, measured on work/wave2c-gates). " +
            "WP8 (docs/plans/2026-08-14-code-quality-improvement-plan.md) is the decomposition — split it, don't raise the cap.");
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
