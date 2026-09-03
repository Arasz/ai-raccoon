using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Tests;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sync;

/// <summary>
///     Pins <see cref="SyncService.FoldRemoteProjectId" />'s generated CASE (d-426 SHOULD-3): alias
///     arms fold losers, canonical self-arms catch winner-resolved names (a remote guid row whose
///     projects-row name is the winner must land on the winner, not leak as a raw guid), and every
///     interpolated literal is quote-escaped — the aliases are compile-time constants, never bank
///     content, but a future quote in the table must not break the merge.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FoldRemoteProjectIdTests
{
    [Fact]
    public void FoldRemoteProjectId_ContainsCanonicalSelfArms()
    {
        // Ledger — missing-self-arm : --filter FoldRemoteProjectId_ContainsCanonicalSelfArms : Default table.
        var sql = SyncService.FoldRemoteProjectId("r.project_id");

        sql.ShouldContain("WHEN 'jsaa' THEN 'jsaa'");
        sql.ShouldContain("WHEN 'ai-raccoon' THEN 'ai-raccoon'");
    }

    [Fact]
    public void FoldRemoteProjectId_KeepsTheAliasArmsAheadOfTheSelfArms()
    {
        // Ledger — self-arm-shadows-alias : --filter FoldRemoteProjectId_KeepsTheAliasArmsAheadOfTheSelfArms : Default table.
        // CASE evaluates in order: an overlap must resolve to the alias winner, never the identity.
        var sql = SyncService.FoldRemoteProjectId("r.project_id");

        sql.IndexOf("WHEN 'job-search-ai-assistant' THEN 'jsaa'", StringComparison.Ordinal)
            .ShouldBeLessThan(sql.IndexOf("WHEN 'jsaa' THEN 'jsaa'", StringComparison.Ordinal));
    }

    [Fact]
    public void EscapeSqlString_DoublesSingleQuotes()
    {
        // Ledger — unescaped-arm : --filter EscapeSqlString_DoublesSingleQuotes : quote-bearing id.
        SyncService.EscapeSqlString("o'brien").ShouldBe("o''brien");
    }
}
