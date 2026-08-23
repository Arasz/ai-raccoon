using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     docs/plans/2026-08-08-search-knn-perf.md §3.3: chunk_index/total_chunks are recomputed at
///     the mutation boundary (write/share/delete/sync-merge), not per query — paths that change a
///     (ctx, source_file) group carry the recompute; paths that remove a whole group or context don't.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreChunkColumnMaintenanceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-chunk-maintenance-tests");
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private SqliteWorkspaceStore _workspaces = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _workspaces = new SqliteWorkspaceStore(_factory);
        await AllowIngestScopeAsync(_dataRoot);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     A chunk shared into the shared context carries the same source_file as its project-scoped
    ///     siblings; a recompute keyed off source_file alone — rather than (ctx, source_file) — would
    ///     incorrectly renumber the project group using the shared row too.
    /// </summary>
    [Fact]
    public async Task ShareAsync_OfOneChunk_NumbersItAloneInItsOwnContext_AndLeavesTheProjectGroupUntouched()
    {
        var file = Path.Combine(_dataRoot, "three-chunks.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var projectRowsBefore = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file);
        projectRowsBefore.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0, 3), (1, 3), (2, 3)]);

        var middleChunk = projectRowsBefore.Single(r => r.ChunkIndex == 1);
        var shared = await _store.ShareAsync("acme", middleChunk.Hash, TestContext.Current.CancellationToken);

        var sharedRows = await ChunkRowsForAsync(ContextNaming.SharedContext, "acme", file);
        var sharedRow = sharedRows.Single(r => r.Hash == shared.Entry.Hash);
        (sharedRow.ChunkIndex, sharedRow.TotalChunks).ShouldBe((0, 1), "the shared row is alone in its own context");

        var projectRowsAfter = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file);
        projectRowsAfter.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0, 3), (1, 3), (2, 3)],
            "sharing a chunk must not renumber the project-scoped group it came from");
    }

    [Fact]
    public async Task IngestFile_FiveChunks_NumbersThemContiguouslyFromZero()
    {
        var file = Path.Combine(_dataRoot, "five-chunks.md");
        await File.WriteAllTextAsync(file,
            string.Join("\n\n", Enumerable.Range(1, 5).Select(i => $"paragraph {i}")),
            TestContext.Current.CancellationToken);

        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var rows = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file);
        rows.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0, 5), (1, 5), (2, 5), (3, 5), (4, 5)]);
    }

    /// <summary>Catches a C#-loop-index implementation: re-ingesting an unchanged file hits the existing-chunk `continue` skip for every chunk, so the loop never touches a new row — the recompute must still leave numbering correct.</summary>
    [Fact]
    public async Task IngestFile_ReIngestingAnUnchangedFile_LeavesNumberingUnchanged()
    {
        var file = Path.Combine(_dataRoot, "five-chunks.md");
        await File.WriteAllTextAsync(file,
            string.Join("\n\n", Enumerable.Range(1, 5).Select(i => $"paragraph {i}")),
            TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var rows = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file);
        rows.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0, 5), (1, 5), (2, 5), (3, 5), (4, 5)]);
    }

    [Fact]
    public async Task Write_WithAnExistingSourceFile_AppendsAndGrowsTotalChunks()
    {
        var first = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "first chunk content", SourceFile: "notes.md"),
            TestContext.Current.CancellationToken);
        var second = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "second chunk content", SourceFile: "notes.md"),
            TestContext.Current.CancellationToken);

        var rows = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", "notes.md");
        rows.Single(r => r.Hash == first.Hash).ChunkIndex.ShouldBe(0);
        rows.Single(r => r.Hash == second.Hash).ChunkIndex.ShouldBe(1);
        rows.ShouldAllBe(r => r.TotalChunks == 2);
    }

    /// <summary>
    ///     GH #371: chunk_index was derived from row-id order, not document order. Editing a middle
    ///     paragraph deletes nothing — the unchanged chunks dedup and keep their old (low) ids, while
    ///     the edited paragraph is inserted fresh with the HIGHEST id — so an id-order recompute puts
    ///     the edited paragraph last, not where it actually sits in the document.
    /// </summary>
    [Fact]
    public async Task IngestFile_EditingAParagraphIntoTheMiddle_KeepsChunkIndexInDocumentOrder()
    {
        var file = Path.Combine(_dataRoot, "four-paragraphs.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three\n\npara four",
            TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        // Edit the second paragraph in place — it keeps its document position (index 1) but its
        // content, and therefore its hash, changes.
        await File.WriteAllTextAsync(file, "para one\n\npara two EDITED\n\npara three\n\npara four",
            TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var rows = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file);
        // FileIngestor never purges the row a superseded edit leaves behind (a separate, pre-existing
        // gap outside GH #371) — assert only on the CURRENT document's four chunks, so a stale
        // "para two" row left over from the first ingest cannot mask the ordering assertion below.
        var currentIndexes = new[] { "para one", "para two EDITED", "para three", "para four" }
            .Select(text => rows.Single(r => r.Hash == ContentHash.Of(file, text)).ChunkIndex)
            .ToList();

        currentIndexes.ShouldBe([.. currentIndexes.OrderBy(i => i)],
            "chunk_index must increase in document order, not insertion order");
        currentIndexes.Distinct().Count().ShouldBe(currentIndexes.Count, "no two chunks of the current document share a position");
    }

    [Fact]
    public async Task DeleteAsync_OfAMiddleChunk_RenumbersSurvivorsContiguously()
    {
        var file = Path.Combine(_dataRoot, "three-chunks.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        var before = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file);
        var middle = before.Single(r => r.ChunkIndex == 1);

        await _store.DeleteAsync("acme", middle.Hash, TestContext.Current.CancellationToken);

        var after = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file);
        after.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0, 2), (1, 2)]);
    }

    [Fact]
    public async Task DeleteSourcePathAsync_RemovesTheWholeGroup_WithNoStaleRowsLeftToRenumber()
    {
        var file = Path.Combine(_dataRoot, "three-chunks.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        deleted.ShouldBe(3);
        (await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file)).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteContextAsync_RemovesTheWholeContext_WithNoStaleRowsLeftToRenumber()
    {
        var file = Path.Combine(_dataRoot, "three-chunks.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteContextAsync("acme", ContextNaming.ProjectContext("acme"),
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(3);
        (await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", file)).ShouldBeEmpty();
    }

    /// <summary>
    ///     GH #371: a sourceless row's default moved from (0, 0) to (-1, 0) — 0 is a real, meaningful
    ///     first-chunk position, so a row nothing has ever positioned needs its own "unknown" value,
    ///     not one indistinguishable from a genuine position 0 (docs/plans/2026-08-08-search-knn-perf.md §3.3).
    /// </summary>
    [Fact]
    public async Task ConsolidateAsync_PromotesWithoutASourceFile_LeavingChunkColumnsAtTheUnknownSentinel()
    {
        var workspaceService = new WorkspaceService(_store, _workspaces, new FakeTimeProvider(FixedNow));
        var workspace = await workspaceService.BeginAsync("acme", cancellationToken: TestContext.Current.CancellationToken);
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "scratch note", WorkspaceId: workspace.Id),
            TestContext.Current.CancellationToken);

        await workspaceService.ConsolidateAsync("acme", workspace.Id, ["all"], TestContext.Current.CancellationToken);

        var promoted = await connectionQuerySingleAsync(entry.Hash);
        promoted.SourceFile.ShouldBeNull();
        (promoted.ChunkIndex, promoted.TotalChunks).ShouldBe((-1, 0));

        async Task<ChunkRow> connectionQuerySingleAsync(string hash)
        {
            await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
            return await connection.QuerySingleAsync<ChunkRow>(
                new CommandDefinition(
                    "SELECT hash AS Hash, source_file AS SourceFile, chunk_index AS ChunkIndex, total_chunks AS TotalChunks " +
                    "FROM entries WHERE scope = 'project' AND project_id = 'acme' AND value = 'scratch note'",
                    cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Property check: over a seeded multi-context bank, the persisted columns equal contiguous 0..n-1/n numbering per (ctx, source_file) group, for every context shape at once.</summary>
    [Fact]
    public async Task PersistedChunkColumns_MatchContiguousNumbering_AcrossEveryContextShapeAtOnce()
    {
        var projectFile = Path.Combine(_dataRoot, "project-doc.md");
        await File.WriteAllTextAsync(projectFile, "p1\n\np2\n\np3", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", projectFile, null, TestContext.Current.CancellationToken);

        await _store.AddContentAsync("acme", "shared/a.md", "shared chunk", ContextNaming.SharedContext,
            sourceFile: "shared-doc.md", cancellationToken: TestContext.Current.CancellationToken);
        await _store.AddContentAsync("acme", "custom/a.md", "custom chunk one", "my-label",
            sourceFile: "custom-doc.md", cancellationToken: TestContext.Current.CancellationToken);
        await _store.AddContentAsync("acme", "custom/b.md", "custom chunk two", "my-label",
            sourceFile: "custom-doc.md", cancellationToken: TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = (await connection.QueryAsync<GroupRow>(
                new CommandDefinition(
                    "SELECT id AS Id, scope AS Scope, project_id AS ProjectId, context_label AS ContextLabel, " +
                    "workspace_id AS WorkspaceId, source_file AS SourceFile, chunk_index AS ChunkIndex, total_chunks AS TotalChunks " +
                    "FROM entries WHERE source_file IS NOT NULL",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();

        var byGroup = rows.GroupBy(r =>
            (MemorySql.ContextKeyFor(ContextStringOf(r), r.ProjectId), r.SourceFile));
        foreach (var group in byGroup)
        {
            var ordered = group.OrderBy(r => r.Id).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i].ChunkIndex.ShouldBe(i);
                ordered[i].TotalChunks.ShouldBe(ordered.Count);
            }
        }
    }

    /// <summary>
    ///     After source normalization, entries have both source_id and the denormalized source_file column.
    ///     Chunk recompute still partitions by (ctx, source_file) — the denormalized column — not by source_id.
    /// </summary>
    [Fact]
    public async Task RecomputeChunkColumns_AfterSourceNormalization_PartitionsBySourceFile()
    {
        // Write two groups with different source_files — the write path now also sets source_id.
        var first = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "alpha one", SourceFile: "alpha.md"),
            TestContext.Current.CancellationToken);
        var second = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "alpha two", SourceFile: "alpha.md"),
            TestContext.Current.CancellationToken);
        var third = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "beta one", SourceFile: "beta.md"),
            TestContext.Current.CancellationToken);

        var alphaRows = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", "alpha.md");
        alphaRows.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0, 2), (1, 2)]);

        var betaRows = await ChunkRowsForAsync(ContextNaming.ProjectContext("acme"), "acme", "beta.md");
        betaRows.Single().ChunkIndex.ShouldBe(0);
        betaRows.Single().TotalChunks.ShouldBe(1);

        // Verify each entry also has source_id set (normalized).
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var sourceIds = (await connection.QueryAsync<long>(
                new CommandDefinition(
                    "SELECT source_id FROM entries WHERE project_id = 'acme' AND source_file IS NOT NULL ORDER BY id",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();
        sourceIds.ShouldAllBe(id => id > 0, "every source_file-bearing entry must have a source_id");
    }

    private static string ContextStringOf(GroupRow row)
    {
        if (row.WorkspaceId is not null)
        {
            return ContextNaming.WorkspaceContext(row.WorkspaceId);
        }

        return row.Scope switch
        {
            "shared" => ContextNaming.SharedContext,
            "project" => ContextNaming.ProjectContext(row.ProjectId),
            "custom" => row.ContextLabel ?? "",
            _ => ""
        };
    }

    private async Task<List<ChunkRow>> ChunkRowsForAsync(string context, string projectId, string sourceFile)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var bucket = context == ContextNaming.SharedContext
            ? ("shared", projectId, null, null)
            : context.StartsWith("project:", StringComparison.Ordinal)
                ? ("project", context["project:".Length..], (string?)null, (string?)null)
                : ((string?)"custom", projectId, context, null);

        var rows = await connection.QueryAsync<ChunkRow>(
            new CommandDefinition(
                "SELECT hash AS Hash, source_file AS SourceFile, chunk_index AS ChunkIndex, total_chunks AS TotalChunks " +
                "FROM entries WHERE scope IS @scope AND project_id = @projectId AND context_label IS @contextLabel " +
                "AND source_file = @sourceFile ORDER BY id",
                new { scope = bucket.Item1, projectId = bucket.Item2, contextLabel = bucket.Item3, sourceFile },
                cancellationToken: TestContext.Current.CancellationToken));
        return [.. rows];
    }

    private Task AllowIngestScopeAsync(string path) =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([path]),
            TestContext.Current.CancellationToken);

    private sealed class ChunkRow
    {
        public string Hash { get; set; } = "";

        public string? SourceFile { get; set; }

        public int ChunkIndex { get; set; }

        public int TotalChunks { get; set; }
    }

    private sealed class GroupRow
    {
        public long Id { get; set; }

        public string? Scope { get; set; }

        public string ProjectId { get; set; } = "";

        public string? ContextLabel { get; set; }

        public string? WorkspaceId { get; set; }

        public string? SourceFile { get; set; }

        public int ChunkIndex { get; set; }

        public int TotalChunks { get; set; }
    }
}
