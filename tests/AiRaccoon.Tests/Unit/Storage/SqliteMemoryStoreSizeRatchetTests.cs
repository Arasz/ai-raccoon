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
    // RAISE HISTORY -- read this before adding a third entry.
    //   1245 / 25  original pin, 2026-08-14, measured after Wave 1's deletions.
    //   1276 / 26  memory_get (ADR-0035) -- closed blocker B2, an agent could find a memory and
    //              had no call returning its content.
    //   1308 / 27  noise shadow-observer wiring (ADR-0039).
    //   1291 / 27  ADR-0046 threaded the project's context labels into SearchAsync; the lookup
    //              moved to SearchContexts.ResolveAsync rather than raising the cap, leaving one
    //              net line for the call itself.
    //   1290 / 27  LOWERED, not raised: the noise_entries write path pushed the file to 1315, and
    //              the note below said the next person should decompose instead of adding a
    //              fourth raise. ISettingsStore/SqliteSettingsStore is WP8's first seam -- the
    //              four settings methods moved out and IMemoryStore delegates, so the ~40 call
    //              sites that reach settings through the store they already hold are untouched.
    //
    // Two raises in a single day of work was the signal this ratchet exists to send, and the
    // third hit was paid down rather than borrowed against: settings came out. Five seams remain
    // (write, search, delete, ingest, embedding). The next person to hit this cap should take one
    // of them rather than add a raise -- raising is borrowing against a decomposition someone
    // still has to pay for.
    private const int MaxLines = 1291;
    private const int MaxMembers = 27;

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
