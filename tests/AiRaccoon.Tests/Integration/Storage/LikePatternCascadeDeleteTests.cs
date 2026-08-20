using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     End-to-end proof for QA-F1 (docs/reviews/2026-08-14-moe-codebase-review.md): the directory-cascade
///     half of <c>MemorySql.DeleteBySourcePath</c> (<c>path LIKE @pathPrefix ESCAPE '\'</c>) must not let
///     a LIKE wildcard character in one file's directory name reach into a sibling directory whose name
///     differs only by that wildcard position — the exact shape of the cross-content delete this repo
///     has already shipped once (docs/reviews/2026-08-14-moe-codebase-review.md, QA-F1).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class LikePatternCascadeDeleteTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-like-cascade-tests");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public LikePatternCascadeDeleteTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory),
            TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
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
}
