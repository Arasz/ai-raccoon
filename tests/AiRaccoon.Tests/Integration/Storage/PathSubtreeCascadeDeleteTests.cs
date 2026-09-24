using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     End-to-end proof for QA-F1 (docs/reviews/2026-08-14-moe-codebase-review.md): the directory-cascade
///     half of <c>MemorySql.DeleteBySourcePath</c> (the <c>@subtreeLow</c>..<c>@subtreeHigh</c> range) must
///     reach only the directory's own subtree — never a sibling whose name shares the directory's as a
///     prefix or differs by a former LIKE wildcard, the cross-content delete this repo has already
///     shipped once (docs/reviews/2026-08-14-moe-codebase-review.md, QA-F1) — and it must seek an
///     index instead of scanning the project's rows.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PathSubtreeCascadeDeleteTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-subtree-cascade-tests");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public PathSubtreeCascadeDeleteTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory),
            TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task DeleteSourcePath_OnADirectory_DoesNotCascadeIntoASiblingDirectory_WhoseNameDiffersOnlyByAWildcardCharacter()
    {
        var ct = TestContext.Current.CancellationToken;

        // "note_1" and "noteX1" are a literal-wildcard-substring pair: an unescaped `_` in the LIKE
        // pattern for "note_1/%" would also match "noteX1/...", because `_` matches any single
        // character in SQLite's LIKE. The victim's directory sits right next to the one being deleted.
        var targetDir = Path.Combine(_dataRoot, "note_1");
        var siblingDir = Path.Combine(_dataRoot, "noteX1");
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(siblingDir);
        var targetFile = Path.Combine(targetDir, "child.md");
        var siblingFile = Path.Combine(siblingDir, "child.md");
        await File.WriteAllTextAsync(targetFile, "phlogopite target directory content", ct);
        await File.WriteAllTextAsync(siblingFile, "vitrescent sibling directory content", ct);

        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);
        await _store.IngestFileAsync("acme", targetFile, null, ct);
        await _store.IngestFileAsync("acme", siblingFile, null, ct);

        var deleted = await _store.DeleteSourcePathAsync("acme", targetDir, ct);

        deleted.ShouldBeGreaterThan(0, "the target directory's own chunk must be deleted");
        (await _store.SearchAsync(new SearchQuery("acme", "phlogopite"), ct)).Results
            .ShouldBeEmpty("the target directory's content must be gone");
        (await _store.SearchAsync(new SearchQuery("acme", "vitrescent"), ct)).Results
            .ShouldNotBeEmpty("the sibling directory's content — 'noteX1', a literal-wildcard near miss of 'note_1' — must survive");
        (await _store.GetStatsAsync("acme", ct)).EntryCount.ShouldBe(1,
            "exactly the sibling's chunk should remain");
    }

    /// <summary>
    ///     '-' and '.' sort before '/', so a naive "starts with the name" range would reach "docs-old"
    ///     and "docs.v2"; only "docs/…" is the directory's subtree.
    /// </summary>
    [RetryFact]
    public async Task DeleteSourcePath_OnADirectory_ReachesItsSubtree_ButNotASiblingThatSharesItsNameAsAPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = Path.Combine(_dataRoot, "docs");
        var nested = Path.Combine(target, "deep", "nested.md");
        var dashed = Path.Combine(_dataRoot, "docs-old", "dashed.md");
        var dotted = Path.Combine(_dataRoot, "docs.v2", "dotted.md");
        foreach (var (file, word) in new[] { (nested, "chalcedony"), (dashed, "orpiment"), (dotted, "realgar") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, $"{word} content", ct);
        }

        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);
        await _store.IngestFileAsync("acme", nested, null, ct);
        await _store.IngestFileAsync("acme", dashed, null, ct);
        await _store.IngestFileAsync("acme", dotted, null, ct);

        await _store.DeleteSourcePathAsync("acme", target, ct);

        (await _store.SearchAsync(new SearchQuery("acme", "chalcedony"), ct)).Results.ShouldBeEmpty("the nested file is in the subtree");
        (await _store.SearchAsync(new SearchQuery("acme", "orpiment"), ct)).Results.ShouldNotBeEmpty("docs-old is a sibling");
        (await _store.SearchAsync(new SearchQuery("acme", "realgar"), ct)).Results.ShouldNotBeEmpty("docs.v2 is a sibling");
    }

    /// <summary>
    ///     The cascade's cost must not scale with the project: every statement DeleteSourcePathAsync
    ///     runs is answered by index seeks, never a scan of entries, code_entries or watch_files.
    /// </summary>
    [RetryTheory]
    [InlineData(nameof(MemorySql.DeleteBySourcePath))]
    [InlineData(nameof(MemorySql.DeleteCodeBySourcePath))]
    [InlineData(nameof(MemorySql.DeleteWatchFilesByProjectPathCascade))]
    [InlineData(nameof(MemorySql.DeleteAllCodeChunksForPath))]
    [InlineData(nameof(MemorySql.DeleteCodeChunksForPathExcept))]
    [InlineData(nameof(MemorySql.SelectWatchFileAtOrUnder))]
    public async Task PathCascade_SeeksAnIndex_InsteadOfScanningTheProject(string statement)
    {
        var ct = TestContext.Current.CancellationToken;
        var sql = (string)typeof(MemorySql).GetField(statement)!.GetValue(null)!;
        const string path = "/repo/docs";
        await using var connection = await _factory.OpenBankAsync(ct);

        var plan = (await connection.QueryAsync<(long Id, long Parent, long NotUsed, string Detail)>(
            new CommandDefinition("EXPLAIN QUERY PLAN " + sql,
                new { projectId = "acme", path, subtreeLow = PathSubtree.Low(path), subtreeHigh = PathSubtree.High(path), keep = new[] { "h" } },
                cancellationToken: ct))).Select(r => r.Detail).ToList();

        plan.ShouldNotContain(d => d.StartsWith("SCAN ", StringComparison.Ordinal) && d != "SCAN CONSTANT ROW", string.Join(" | ", plan));
        plan.ShouldContain(d => d.Contains("path>? AND path<?", StringComparison.Ordinal), string.Join(" | ", plan));
    }
}
