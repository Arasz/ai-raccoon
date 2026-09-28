using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     config-D P1b (plan §3 rows 5, 19, 20): the chunk-budget rebudget pass re-chunks existing
///     groups at the resolved budget — note groups by <see cref="ChunkReassembly" />'s proven merge
///     (byte-identical, sha256 == the path stem), mirror groups from disk — replacing each group in
///     one transaction that carries the insert spec forward (never <c>ChunkBackfill</c>'s nulls).
///     Unproven note groups are untouched and retryable-skipped; vanished source files are untouched
///     and terminal-skipped; the <c>embedding.chunkBudget</c> stamp is written only at zero
///     retryable skips.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkRebudgetTests : IDisposable
{
    private const string ProjectId = "acme";
    private const int OldBudget = 254;
    private const int NewBudget = 1022;
    private const string StampKey = "embedding.chunkBudget";

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-rebudget");
    private readonly SqliteConnectionFactory _factory;
    private readonly CountingEmbeddingService _embeddings = new();
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly SqliteMemoryStore _store;
    private readonly ChunkBudgetReconciler _reconciler;

    public ChunkRebudgetTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _embeddings.ChunkBudgetOverride = OldBudget;
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), _time,
            _embeddings, null, null, null, null, null, null, null);
        _reconciler = new ChunkBudgetReconciler(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            _embeddings, _time, () => _store);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Long enough to split at 254 tokens and merge into one piece at 1,022.</summary>
    private static string LongNote() => string.Join("\n\n", Enumerable.Range(0, 30).Select(i =>
        $"Item {i:D2} records that the committee approved the tombola budget line {i * 7:D3} after a vote."));

    private static string NotePath(string content) => $"{ContentHash.OfValue(content)}.md";

    private static string FileBody() =>
        string.Join("\n\n", Enumerable.Range(1, 5).Select(i =>
            $"## Section {i}\n\n" + string.Join(" ", Enumerable.Repeat($"magnetostrictive section {i} body text", 40))));

    private int CountTokens(string value) =>
        _embeddings.ResolveTokenizer(new EmbeddingSettings("local", null, null, null))!.CountTokens(value);

    private async Task<SqliteConnection> OpenAsync() => await _factory.OpenBankAsync(Ct);

    private static async Task<List<Row>> RowsAsync(SqliteConnection connection, string? path = null) =>
        [
            .. await connection.QueryAsync<Row>(new CommandDefinition(
                """
                SELECT id AS Id, hash AS Hash, value AS Value, embed_state AS EmbedState, agent_id AS AgentId,
                       source_file AS SourceFile, section AS Section, source_id AS SourceId, created_at AS CreatedAt,
                       chunk_index AS ChunkIndex, total_chunks AS TotalChunks
                FROM entries WHERE (@path IS NULL OR path = @path) ORDER BY id
                """, new { path }, cancellationToken: Ct))
        ];

    /// <summary>Every row of one path with its bucket columns, so a two-context case can be asserted per bucket.</summary>
    private static async Task<List<BucketRow>> BucketRowsAsync(SqliteConnection connection, string path) =>
        [
            .. await connection.QueryAsync<BucketRow>(new CommandDefinition(
                """
                SELECT id AS Id, hash AS Hash, value AS Value, scope AS Scope, context_label AS ContextLabel,
                       workspace_id AS WorkspaceId, embed_state AS EmbedState
                FROM entries WHERE path = @path ORDER BY id
                """, new { path }, cancellationToken: Ct))
        ];

    private static async Task<string?> StampAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key", new { key = StampKey }, cancellationToken: Ct));

    private IReadOnlyList<string> ExpectedPieces(string content, int budget) =>
        TestData.RealMarkdownChunker().Chunk(content, budget, ChunkingDefaults.OverlayTokens, CountTokens);

    [RetryFact]
    public async Task Rebudget_ANoteWrittenAt254_BecomesBudgetPiecesWithByteIdenticalMergedContent()
    {
        var content = LongNote();
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content, AgentId: "agent-9"), Ct);
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded'", cancellationToken: Ct));
        var before = await RowsAsync(connection);
        before.Count.ShouldBeGreaterThan(1, "premise: the note splits at 254");
        before.ShouldAllBe(row => row.EmbedState == "embedded");
        _embeddings.ChunkBudgetOverride = NewBudget;
        _time.Advance(TimeSpan.FromDays(2)); // carried created_at must survive a later replace

        var report = await _reconciler.RunAsync(connection, Ct);

        report.Budget.ShouldBe(NewBudget);
        report.NoteGroupsRechunked.ShouldBe(1);
        var after = await RowsAsync(connection);
        after.Count.ShouldBeLessThan(before.Count, "the pieces merge at the larger budget");
        after.Count.ShouldBe(ExpectedPieces(content, NewBudget).Count);
        after.ShouldAllBe(row => CountTokens(row.Value) <= NewBudget);
        after.ShouldAllBe(row => row.EmbedState == "pending", "replacement rows arrive pending");
        var merged = ChunkReassembly.TryMerge(NotePath(content),
            [.. after.Select(row => new NoteRow(row.Id, row.Value, -1))]);
        merged.ShouldBe(content, "merged content must be byte-identical to the note as written (AC1)");
        ContentHash.OfValue(merged!).ShouldBe(Path.GetFileNameWithoutExtension(NotePath(content)));
        after.ShouldAllBe(row => row.AgentId == "agent-9" && row.SourceFile == null && row.CreatedAt == FixedNow.ToUnixTimeSeconds());
    }

    [RetryFact]
    public async Task Rebudget_ACitingNotesReplacement_KeepsTextOrderAndCarriesTheInsertSpecColumns()
    {
        var content = LongNote();
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content, AgentId: "scribe",
            SourceFile: "docs/minutes.md", Section: "## Budget"), Ct);
        await using var connection = await OpenAsync();
        var before = await RowsAsync(connection);
        before.Count.ShouldBeGreaterThan(1, "premise: the note splits at 254");
        var writtenAt = before[0].CreatedAt;
        _embeddings.ChunkBudgetOverride = NewBudget;
        _time.Advance(TimeSpan.FromDays(3));

        var report = await _reconciler.RunAsync(connection, Ct);

        report.NoteGroupsRechunked.ShouldBe(1);
        var after = await RowsAsync(connection);
        var pieces = ExpectedPieces(content, NewBudget);
        after.Select(row => row.Value).ShouldBe(pieces, "insert order = text order (ADR-0122's write convention)");
        after.ShouldAllBe(row => row.SourceFile == "docs/minutes.md" && row.Section == "## Budget"
                                && row.AgentId == "scribe" && row.SourceId != null
                                && row.CreatedAt == writtenAt);
        after.Select(row => row.ChunkIndex).ShouldBe(after.Select(row => row.ChunkIndex).Order(),
            "the group-scoped column repair leaves the note's rows in text order");
        after.ShouldAllBe(row => row.ChunkIndex >= 0 && row.TotalChunks > 0);
    }

    [RetryFact]
    public async Task Rebudget_SingleChunkGroup_IsUntouched()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "A short note that fits comfortably."), Ct);
        await using var connection = await OpenAsync();
        var before = await RowsAsync(connection);
        before.Count.ShouldBe(1);
        _embeddings.ChunkBudgetOverride = NewBudget;

        var report = await _reconciler.RunAsync(connection, Ct);

        report.NoteGroupsRechunked.ShouldBe(0);
        report.GroupsUnchanged.ShouldBe(1);
        (await RowsAsync(connection)).Select(row => row.Id).ShouldBe(before.Select(row => row.Id),
            "a single-chunk group keeps its row");
    }

    [RetryFact]
    public async Task Rebudget_UnprovenGroup_IsUntouchedCountedRetryableSkippedAndWithholdsTheStamp()
    {
        var content = LongNote();
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), Ct);
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = value || ' tampered' WHERE id = (SELECT MIN(id) FROM entries)",
            cancellationToken: Ct));
        var before = await RowsAsync(connection);
        _embeddings.ChunkBudgetOverride = NewBudget;

        var report = await _reconciler.RunAsync(connection, Ct);

        report.RetryableSkipped.ShouldBe(1, "a group no join proves is counted retryable-skipped (AC1)");
        report.NoteGroupsRechunked.ShouldBe(0);
        (await RowsAsync(connection)).Select(row => row.Id).ShouldBe(before.Select(row => row.Id),
            "an unproven group is left untouched");
        (await StampAsync(connection)).ShouldBeNull("retryable skips withhold the stamp (AC7)");
    }

    [RetryFact]
    public async Task Rebudget_FailureBetweenDeleteAndInsert_LeavesTheOldRows()
    {
        var content = LongNote();
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, content), Ct);
        await using var connection = await OpenAsync();
        var before = await RowsAsync(connection);
        before.Count.ShouldBeGreaterThan(1, "premise: the replace has deletes and inserts to fail between");
        _embeddings.ChunkBudgetOverride = NewBudget;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TRIGGER rebudget_insert_guard BEFORE INSERT ON entries
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END
            """, cancellationToken: Ct));

        await Should.ThrowAsync<SqliteException>(() => _reconciler.RunAsync(connection, Ct));

        (await RowsAsync(connection)).Select(row => (row.Id, row.Hash, row.Value)).ShouldBe(
            before.Select(row => (row.Id, row.Hash, row.Value)),
            "the group's replacement is one transaction: a failure between delete and insert leaves the old rows (AC3)");

        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TRIGGER rebudget_insert_guard", cancellationToken: Ct));
        (await _reconciler.RunAsync(connection, Ct)).NoteGroupsRechunked.ShouldBe(1,
            "with the failure gone the same pass replaces the group");
    }

    [RetryFact]
    public async Task Rebudget_SecondRun_IsANoOp()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await using var connection = await OpenAsync();
        _embeddings.ChunkBudgetOverride = NewBudget;

        (await _reconciler.RunAsync(connection, Ct)).NoteGroupsRechunked.ShouldBe(1);
        var afterFirst = await RowsAsync(connection);
        _time.Advance(TimeSpan.FromDays(1));

        var second = await _reconciler.RunAsync(connection, Ct);

        second.NoteGroupsRechunked.ShouldBe(0, "the hash-set equality skip makes the second run a no-op (AC4)");
        second.GroupsUnchanged.ShouldBe(1);
        (await RowsAsync(connection)).Select(row => row.Id).ShouldBe(afterFirst.Select(row => row.Id));
    }

    [RetryFact]
    public async Task Rebudget_TheSameFileInTwoContexts_RechunksEachBucketInPlace()
    {
        var file = Path.Combine(_dataRoot, "twice.md");
        await File.WriteAllTextAsync(file, FileBody(), Ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), Ct);
        await _store.IngestFileAsync(ProjectId, file, null, Ct);
        await _store.IngestFileAsync(ProjectId, file, "alpha", Ct);
        await using var connection = await OpenAsync();
        var before = await BucketRowsAsync(connection, file);
        before.Count(row => row.Scope == "project").ShouldBeGreaterThan(1, "premise: the file splits at 254");
        before.Count(row => row.Scope == "custom" && row.ContextLabel == "alpha")
            .ShouldBe(before.Count(row => row.Scope == "project"));
        _embeddings.ChunkBudgetOverride = NewBudget;

        var report = await _reconciler.RunAsync(connection, Ct);

        report.MirrorGroupsRechunked.ShouldBe(2, "one mirror group per context (config-D F1)");
        var after = await BucketRowsAsync(connection, file);
        var projectAfter = after.Where(row => row.Scope == "project").ToList();
        var alphaAfter = after.Where(row => row.Scope == "custom" && row.ContextLabel == "alpha").ToList();
        projectAfter.Count.ShouldBeLessThan(before.Count(row => row.Scope == "project"),
            "the project bucket re-chunks at the resolved budget");
        alphaAfter.Count.ShouldBe(projectAfter.Count, "the custom bucket is preserved, not relabelled or deleted");
        alphaAfter.ShouldAllBe(row => row.EmbedState == "pending", "its replacement rows arrive pending too");
    }

    [RetryFact]
    public async Task Rebudget_MirrorGroups_RechunkFromDiskAndVanishedFilesAreTerminalSkipped()
    {
        var kept = Path.Combine(_dataRoot, "kept.md");
        var gone = Path.Combine(_dataRoot, "gone.md");
        await File.WriteAllTextAsync(kept, FileBody(), Ct);
        await File.WriteAllTextAsync(gone, FileBody(), Ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), Ct);
        await _store.IngestFileAsync(ProjectId, kept, null, Ct);
        await _store.IngestFileAsync(ProjectId, gone, null, Ct);
        File.Delete(gone);
        await using var connection = await OpenAsync();
        var keptBefore = await RowsAsync(connection, kept);
        var goneBefore = await RowsAsync(connection, gone);
        keptBefore.Count.ShouldBeGreaterThan(1, "premise: the file splits at 254");
        _embeddings.ChunkBudgetOverride = NewBudget;

        var report = await _reconciler.RunAsync(connection, Ct);

        report.MirrorGroupsRechunked.ShouldBe(1, "the file rows re-chunk from disk at the resolved budget (AC2)");
        report.TerminalSkipped.ShouldBe(1, "a vanished source file is counted terminal-skipped (AC2)");
        var keptAfter = await RowsAsync(connection, kept);
        keptAfter.Count.ShouldBeLessThan(keptBefore.Count);
        keptAfter.ShouldAllBe(row => CountTokens(row.Value) <= NewBudget);
        (await RowsAsync(connection, gone)).Select(row => row.Id).ShouldBe(goneBefore.Select(row => row.Id),
            "rows whose source file is gone are untouched");
        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(),
            "terminal skips never block the stamp (AC7)");

        (await _reconciler.RunAsync(connection, Ct)).MirrorGroupsRechunked.ShouldBe(0,
            "the replaced rows reproduce at the resolved budget, so a second run is a no-op");
    }

    [RetryFact]
    public async Task Rebudget_TheStampIsWrittenAtZeroRetryableSkips_AfterALaterRunConverges()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "Second note.\n\n" + LongNote()), Ct);
        await using var connection = await OpenAsync();
        var brokenPath = NotePath("Second note.\n\n" + LongNote());
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = value || ' tampered' WHERE id = (SELECT MIN(id) FROM entries WHERE path = @path)",
            new { path = brokenPath }, cancellationToken: Ct));
        _embeddings.ChunkBudgetOverride = NewBudget;

        var first = await _reconciler.RunAsync(connection, Ct);

        first.RetryableSkipped.ShouldBe(1);
        first.NoteGroupsRechunked.ShouldBe(1, "the healthy group still re-chunks");
        (await StampAsync(connection)).ShouldBeNull("the stamp is written only at zero retryable skips (AC7)");

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM entries WHERE path = @path", new { path = brokenPath }, cancellationToken: Ct));
        _time.Advance(TimeSpan.FromHours(1));

        var second = await _reconciler.RunAsync(connection, Ct);

        second.RetryableSkipped.ShouldBe(0);
        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(),
            "a later run converges and then stamps");
    }

    [RetryFact]
    public async Task Rebudget_AMirrorFileInAWorkspace_RechunksInsideItsWorkspaceBucket()
    {
        var file = Path.Combine(_dataRoot, "workspace-file.md");
        await File.WriteAllTextAsync(file, FileBody(), Ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), Ct);
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-7', @projectId, 'active', 0)",
            new { projectId = ProjectId }, cancellationToken: Ct));
        await _store.IngestFileAsync(ProjectId, file, ContextNaming.WorkspaceContext("ws-7"), Ct);
        var before = await BucketRowsAsync(connection, file);
        before.Count.ShouldBeGreaterThan(1, "premise: the file splits at 254");
        before.ShouldAllBe(row => row.Scope == null && row.WorkspaceId == "ws-7",
            "premise: the rows live in the workspace bucket");
        _embeddings.ChunkBudgetOverride = NewBudget;

        var report = await _reconciler.RunAsync(connection, Ct);

        report.MirrorGroupsRechunked.ShouldBe(1,
            "the workspace-row policy is re-chunk per bucket (config-D F2) — counted, never silently skipped");
        var after = await BucketRowsAsync(connection, file);
        after.Count.ShouldBeLessThan(before.Count, "the workspace bucket re-chunks at the resolved budget");
        after.ShouldAllBe(row => row.Scope == null && row.WorkspaceId == "ws-7",
            "the rows stay in their workspace bucket — never moved to the project context");
        after.ShouldAllBe(row => row.EmbedState == "pending", "its replacement rows arrive pending too");
    }

    private sealed record Row(long Id, string Hash, string Value, string EmbedState, string? AgentId,
        string? SourceFile, string? Section, long? SourceId, long CreatedAt, long ChunkIndex, long TotalChunks);

    private sealed record BucketRow(long Id, string Hash, string Value, string? Scope, string? ContextLabel,
        string? WorkspaceId, string EmbedState);
}
