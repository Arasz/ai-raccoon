using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Tests;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sync;

/// <summary>
///     Pins <see cref="SyncService.FoldRemoteProjectId" />'s generated CASE (d-426 SHOULD-3): alias
///     arms fold losers, canonical self-arms catch winner-resolved names (a remote guid row whose
///     projects-row name is the winner must land on the winner, not leak as a raw guid), and every
///     interpolated literal is quote-escaped — the entries are operator-supplied per repair, never
///     bank content, but a future quote in the table must not break the merge.
///     Machine ids below are explicit fixture data (ADR-0099); production Default is empty.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FoldRemoteProjectIdTests
{
    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
        ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
        ["qa-noise-project", "manual-sweep"]);

    [Fact]
    public void FoldRemoteProjectId_ContainsCanonicalSelfArms()
    {
        // Ledger — missing-self-arm : --filter FoldRemoteProjectId_ContainsCanonicalSelfArms : fixture table.
        var sql = SyncService.FoldRemoteProjectId("r.project_id", FixtureMap());

        sql.ShouldContain("WHEN 'jsaa' THEN 'jsaa'");
        sql.ShouldContain("WHEN 'ai-raccoon' THEN 'ai-raccoon'");
    }

    [Fact]
    public void FoldRemoteProjectId_KeepsTheAliasArmsAheadOfTheSelfArms()
    {
        // Ledger — self-arm-shadows-alias : --filter FoldRemoteProjectId_KeepsTheAliasArmsAheadOfTheSelfArms : fixture table.
        // CASE evaluates in order: an overlap must resolve to the alias winner, never the identity.
        var sql = SyncService.FoldRemoteProjectId("r.project_id", FixtureMap());

        sql.IndexOf("WHEN 'job-search-ai-assistant' THEN 'jsaa'", StringComparison.Ordinal)
            .ShouldBeLessThan(sql.IndexOf("WHEN 'jsaa' THEN 'jsaa'", StringComparison.Ordinal));
    }

    [Fact]
    public void FoldRemoteProjectId_WithTheEmptyDefault_ReturnsNameResolutionWithoutCase()
    {
        // ADR-0099: SQLite rejects CASE with zero WHEN arms — the empty map degrades to the
        // remote-projects name resolution alone, and the SQL must execute on real SQLite.
        var sql = SyncService.FoldRemoteProjectId("r.project_id", ProjectIdAliasMap.Empty);

        sql.ShouldNotContain("CASE");
        sql.ShouldContain("remote.projects");
        ExecuteOnRealSqlite(sql);
    }

    [Fact]
    public void EscapeSqlString_DoublesSingleQuotes()
    {
        // Ledger — unescaped-arm : --filter EscapeSqlString_DoublesSingleQuotes : quote-bearing id.
        SyncService.EscapeSqlString("o'brien").ShouldBe("o''brien");
    }

    private static void ExecuteOnRealSqlite(string expression)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE entries (project_id TEXT); ATTACH DATABASE ':memory:' AS remote; " +
                "CREATE TABLE remote.projects (id TEXT, name TEXT); INSERT INTO entries VALUES ('x');";
            setup.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {expression} FROM entries r";
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("x");
    }
}
