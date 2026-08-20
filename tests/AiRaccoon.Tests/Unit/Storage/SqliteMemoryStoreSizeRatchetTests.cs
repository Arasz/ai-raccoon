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
    //   1251 / 27  LOWERED, not raised: WP1's project-confinement guard pushed the file to 1298 and
    //              this cap caught it. Taking the note below at its word, the *delete* seam came
    //              out instead of a raise -- FilterFor moved to ContextFilter.cs, next to its
    //              write-path twin EntryBucket, which is also where the review said the mapping
    //              belonged (the two halves of one rule sat 1,000 lines apart in different files).
    //   1238 / 27  LOWERED again: WP3 step 1 routed memory_write through the budgeted chunker and
    //               put the per-chunk insert in WriteChunks.cs beside EntryBucket/ContextFilter
    //               rather than in the store, so the chunking landed net -5 (docs/adr/0064).
    //   1243 / 27  LOWERED again: WP6 folded the rating computation into the BumpAccess statement,
    //              which removed the SELECT, the RatingRow type and the C# arithmetic between them.
    //              A fix that deletes more than it adds is the shape to prefer here.
    //   1214 / 27  LOWERED again, and this cap caught a RED main: WP9's injected ISettingsStore
    //              parameter pushed the file to 1239 against a 1237 cap. The PR's own build-fast
    //              failed and was merged anyway, so the ratchet was doing its job and the merge
    //              overrode it -- worth noting, because a gate that only fails after the fact still
    //              only works if someone reads it. Taking the note below at its word for the fifth
    //              time, BuildJsonTree came out to FileTree.cs instead of a raise: a pure function
    //              over paths that never touched the bank, so it was never the store's to hold, and
    //              it is now directly testable (FileTreeTests) where before it had only indirect
    //              coverage through ListFilesAsync. The unused System.Text.Json using went with it.
    //
    //   1181 / 27  LOWERED again, and this one is a correction rather than a seam: ADR-0073's fix
    //              is one argument, and I wrote a three-line comment above it explaining why. That
    //              pushed the file to 1217 and turned main red on exactly this gate. The comment was
    //              the inline rationale minimal-comments forbids when an ADR already carries it, so
    //              the fix was to delete it -- and then every other standalone // in the file, 37 of
    //              them, for the same reason. The XML doc comments stay: they state contracts.

    // Two raises in a single day of work was the signal this ratchet exists to send, and the
    // third hit was paid down rather than borrowed against: settings came out. Four seams remain
    // (write, search, ingest, embedding). The next person to hit this cap should take one
    // of them rather than add a raise -- raising is borrowing against a decomposition someone
    // still has to pay for.
    //   1112 / 27  LOWERED again, by two reductions that landed independently and compounded:
    //              main's ADR-0073 comment sweep (1181) and this branch's move of the nine
    //              Dapper row DTOs to SqliteMemoryStore.Rows.cs, which made room for the search
    //              phase timings (1146). Measured on the merge of both, not on either alone.
    //   1066 / 27  LOWERED again, and this cap caught the change that provoked it: the
    //              no-fusion-regression flag (docs/adr/0078) pushed the file to 1144 against the
    //              1112 cap. Taking the note above at its word for the sixth time, the *search*
    //              seam came out instead of a raise — CandidateWindowFor, QueryFtsBatchAsync,
    //              BuildFtsResults, ReadStructureAlphaAsync and the flag's own settings read moved
    //              to SqliteMemoryStore.Search.cs, the same partial-file seam SqliteMemoryStore.Rows.cs
    //              already established. Net −46 against the pre-change file.
    private const int MaxLines = 1066;
    private const int MaxMembers = 27;

    [Fact]
    public void SqliteMemoryStore_DoesNotExceedItsMeasuredLineCap()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "AiRaccoon.Infrastructure", "Sqlite", "Memory", "SqliteMemoryStore.cs");
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
