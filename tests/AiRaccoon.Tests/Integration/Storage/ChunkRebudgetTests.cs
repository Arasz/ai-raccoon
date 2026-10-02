using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
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
///     retryable skips. The F5 retry bound: an unproven group spends at most three passes before it
///     is counted unprovable — terminal for this budget — and stops gating the stamp.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkRebudgetTests : IDisposable
{
    private const string ProjectId = "acme";
    private const int OldBudget = 254;
    private const int NewBudget = 1022;
    private const string StampKey = "embedding.chunkBudget";
    private const string RetryAttemptsKey = "embedding.chunkBudget.retryAttempts";
    private const int RebudgetEventId = 448;

    private static readonly DateTimeOffset FixedNow = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-rebudget");
    private readonly SqliteConnectionFactory _factory;
    private readonly CountingEmbeddingService _embeddings = new();
    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly SqliteMemoryStore _store;
    private readonly ChunkBudgetReconciler _reconciler;
    private readonly FakeLogger<ChunkBudgetReconciler> _log = new();

    public ChunkRebudgetTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _embeddings.ChunkBudgetOverride = OldBudget;
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), _time,
            _embeddings, null, null, null, null, null, null, null);
        _reconciler = NewReconciler(_time);
    }

    /// <summary>One reconciler shape, so a test can swap only the clock the phase measures itself with.</summary>
    private ChunkBudgetReconciler NewReconciler(TimeProvider timeProvider) =>
        new(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(), _embeddings, timeProvider, () => _store, _log);

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Long enough to split at 254 tokens and merge into one piece at 1,022.</summary>
    private static string LongNote() => string.Join("\n\n", Enumerable.Range(0, 30).Select(i =>
        $"Item {i:D2} records that the committee approved the tombola budget line {i * 7:D3} after a vote."));

    private static string NotePath(string content) => $"{ContentHash.OfValue(content)}.md";

    private static string FileBody() =>
        string.Join("\n\n", Enumerable.Range(1, 5).Select(i =>
            $"## Section {i}\n\n{string.Join(" ", Enumerable.Repeat($"magnetostrictive section {i} body text", 40))}"));

    private int CountTokens(string value) =>
        _embeddings.ResolveTokenizer(new EmbeddingSettings("local", null, null, null))!.CountTokens(value);

    private async Task<SqliteConnection> OpenAsync() => await _factory.OpenBankAsync(Ct);

    private static async Task<List<Row>> RowsAsync(SqliteConnection connection, string? path = null) =>
        [
            .. await connection.QueryAsync<Row>(new CommandDefinition(
                """
                SELECT id AS Id, hash AS Hash, value AS Value, embed_state AS EmbedState, agent_id AS AgentId,
                       source_file AS SourceFile, section AS Section, source_id AS SourceId, created_at AS CreatedAt,
                       chunk_index AS ChunkIndex, total_chunks AS TotalChunks, ttl_days AS TtlDays
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

    private static async Task<string?> RetryAttemptsAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key", new { key = RetryAttemptsKey }, cancellationToken: Ct));

    private IReadOnlyList<FakeLogRecord> LoggedRebudgetRecords() =>
        [.. _log.Collector.GetSnapshot().Where(record => record.Id.Id == RebudgetEventId)];

    private IReadOnlyList<string> LoggedRebudgetMessages() =>
        [.. LoggedRebudgetRecords().Select(record => record.Message)];

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
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, $"Second note.\n\n{LongNote()}"), Ct);
        await using var connection = await OpenAsync();
        var brokenPath = NotePath($"Second note.\n\n{LongNote()}");
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

    [RetryFact]
    public async Task Rebudget_ANoteWithTtlDays_CarriesTheTtlToItsRechunkedRows()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET ttl_days = 7", cancellationToken: Ct));
        var before = await RowsAsync(connection);
        before.Count.ShouldBeGreaterThan(1, "premise: the note splits at 254");
        before.ShouldAllBe(row => row.TtlDays == 7, "premise: every stored row carries the TTL");
        _embeddings.ChunkBudgetOverride = NewBudget;

        var report = await _reconciler.RunAsync(connection, Ct);

        report.NoteGroupsRechunked.ShouldBe(1);
        var after = await RowsAsync(connection);
        after.ShouldAllBe(row => row.TtlDays == 7,
            "ttl_days is the per-entry forgetting knob the re-chunk carries forward (config-D F3)");
    }

    /// <summary>The F5 rule: a group no join proves is retried once per pass while the persisted
    /// attempt counter (keyed by the resolved budget) has windows left. The third unproven pass
    /// re-classifies the group unprovable — untouched, terminal for this budget — and writes the
    /// stamp anyway, so a permanently-unprovable group can never re-open the migration forever.</summary>
    [RetryFact]
    public async Task Rebudget_AnUnprovableGroup_SpendsThreeAttempts_ThenStopsGatingTheStamp()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await using var connection = await OpenAsync();
        var victim = (await RowsAsync(connection)).MinBy(row => row.Id)!;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = value || ' tampered' WHERE id = @id",
            new { id = victim.Id }, cancellationToken: Ct));
        var broken = await RowsAsync(connection);
        broken.Count.ShouldBeGreaterThan(1, "premise: the note splits at 254 and its merge cannot prove");
        _embeddings.ChunkBudgetOverride = NewBudget;

        ChunkRebudgetReport third = null!;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var report = await _reconciler.RunAsync(connection, Ct);
            (await RetryAttemptsAsync(connection)).ShouldBe($"{NewBudget}:{attempt}",
                "each unproven pass spends one persisted attempt (three per budget)");
            if (attempt < 3)
            {
                report.RetryableSkipped.ShouldBe(1, "unproven passes are retried while windows remain");
                (await StampAsync(connection)).ShouldBeNull("an unproven group withholds the stamp");
            }

            third = report;
        }

        third.RetryableSkipped.ShouldBe(0,
            "the third pass re-classifies the unproven group out of the retryable count");
        third.UnprovableSkipped.ShouldBe(1,
            "and counts it unprovable — untouched, terminal for this budget (config-D F5)");
        third.NoteGroupsRechunked.ShouldBe(0, "nothing re-chunked from an unproven group");
        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(),
            "the spent bound writes the stamp — terminal skips do not gate it (config-D F5)");
        (await RowsAsync(connection)).Select(row => (row.Id, row.Value))
            .ShouldBe(broken.Select(row => (row.Id, row.Value)), "the unproven group is never touched");

        var fourth = await _reconciler.RunAsync(connection, Ct);

        fourth.RetryableSkipped.ShouldBe(0, "the persisted bound keeps the residual population terminal");
        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(),
            "and it never re-gates the stamp — the migration cannot re-open on it");
    }

    [RetryFact]
    public async Task Rebudget_AGroupThatConvergesOnALaterPass_ClearsItsRetryAttempts()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await using var connection = await OpenAsync();
        var victim = (await RowsAsync(connection)).MinBy(row => row.Id)!;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = value || ' tampered' WHERE id = @id",
            new { id = victim.Id }, cancellationToken: Ct));
        _embeddings.ChunkBudgetOverride = NewBudget;

        (await _reconciler.RunAsync(connection, Ct)).RetryableSkipped.ShouldBe(1,
            "premise: the group is unproven on the first pass");
        (await RetryAttemptsAsync(connection)).ShouldBe($"{NewBudget}:1",
            "the unproven pass spends one attempt");
        (await StampAsync(connection)).ShouldBeNull("an unproven group withholds the stamp");

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = @value WHERE id = @id",
            new { id = victim.Id, value = victim.Value }, cancellationToken: Ct));
        _time.Advance(TimeSpan.FromHours(1));

        var converged = await _reconciler.RunAsync(connection, Ct);

        converged.RetryableSkipped.ShouldBe(0);
        converged.NoteGroupsRechunked.ShouldBe(1, "the proven group re-chunks at the resolved budget");
        (await RetryAttemptsAsync(connection)).ShouldBeNull("a converging pass clears the attempt counter");
        (await StampAsync(connection)).ShouldBe(NewBudget.ToString(), "and stamps the bank");
    }

    [RetryFact]
    public async Task Rebudget_PhaseElapsed_MatchesTheScriptedClock()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await using var connection = await OpenAsync();
        _embeddings.ChunkBudgetOverride = NewBudget;
        // The phase brackets two GetTimestamp reads; the scripted provider advances its cursor
        // between them, so an elapsed measured anywhere but the real bracket reads the wrong number.
        var reconciler = NewReconciler(ScriptedTimeProvider.ForSpan(TimeSpan.FromSeconds(305.5)));

        var report = await reconciler.RunAsync(connection, Ct);

        report.Elapsed.ShouldBe(TimeSpan.FromSeconds(305.5),
            "the phase elapsed is the injected clock's span, not the wall clock's");
        var record = LoggedRebudgetRecords().ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Information,
            "448 is documented at Information — a Debug downgrade hides the line from default logs");
        record.Message.ShouldEndWith(" in 00:05:05.5000000",
            customMessage: "event 448 reads the same Elapsed the report carries");
    }

    /// <summary>F3: the elapsed must bracket the budget scan the phase opens with. The scan consumes
    /// the phase clock through the embedding service's budget resolution, so moving <c>startedAt</c>
    /// after <c>BudgetAsync</c> drops that span — the named kill “a stopwatch starting after the
    /// scans” — and reddens the report value and event 448.</summary>
    [RetryFact]
    public async Task Rebudget_PhaseElapsed_CoversTheBudgetScan()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await using var connection = await OpenAsync();
        _embeddings.ChunkBudgetOverride = NewBudget;
        var scanSpan = TimeSpan.FromSeconds(305.5);
        var clock = new FakeTimeProvider(FixedNow);
        var reconciler = new ChunkBudgetReconciler(TestData.RealFileTypeMatcher(), TestData.RealMarkdownChunker(),
            new BudgetScanAdvancingEmbeddingService(_embeddings, clock, scanSpan), clock, () => _store, _log);

        var report = await reconciler.RunAsync(connection, Ct);

        report.NoteGroupsRechunked.ShouldBe(1);
        report.Elapsed.ShouldBe(scanSpan,
            "startedAt is read before BudgetAsync, so the clock work the scan consumes is inside the phase");
        LoggedRebudgetRecords().ShouldHaveSingleItem().Message.ShouldEndWith(" in 00:05:05.5000000",
            customMessage: "event 448 carries the same scan-inclusive span");
    }

    [RetryFact]
    public async Task Rebudget_Event448_ReadsEveryReportCount()
    {
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, LongNote()), Ct);
        await using var connection = await OpenAsync();
        _embeddings.ChunkBudgetOverride = NewBudget;
        var reconciler = NewReconciler(ScriptedTimeProvider.ForSpan(TimeSpan.FromSeconds(305.5)));

        await reconciler.RunAsync(connection, Ct);

        LoggedRebudgetMessages().ShouldBe([
            "Chunk-budget rebudget at 1022 tokens: 1 note group(s) and 0 mirror group(s) re-chunked, "
            + "0 unchanged, 0 retryable, 0 unprovable, 0 terminal in 00:05:05.5000000"
        ], "the discarded report is one event-448 line per pass carrying every count and the phase elapsed");
        LoggedRebudgetRecords().ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Information,
            "448 is documented at Information — a Debug downgrade hides the line from default logs");
    }

    /// <summary>F1: the all-zero fixture cannot tell the retryable and unprovable bindings apart —
    /// swapping the two arguments in the log call survives it. One healthy note plus one unproven
    /// note makes those counts differ in a single pass (1 vs 0), so the exact line goes red.</summary>
    [RetryFact]
    public async Task Rebudget_Event448_ReadsNonZeroSkipCounts()
    {
        var healthy = LongNote();
        var broken = $"Second note.\n\n{LongNote()}";
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, healthy), Ct);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId, broken), Ct);
        await using var connection = await OpenAsync();
        var brokenPath = NotePath(broken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET value = value || ' tampered' WHERE id = (SELECT MIN(id) FROM entries WHERE path = @path)",
            new { path = brokenPath }, cancellationToken: Ct));
        _embeddings.ChunkBudgetOverride = NewBudget;
        var reconciler = NewReconciler(ScriptedTimeProvider.ForSpan(TimeSpan.FromSeconds(305.5)));

        var report = await reconciler.RunAsync(connection, Ct);

        report.NoteGroupsRechunked.ShouldBe(1, "the healthy group re-chunks");
        report.RetryableSkipped.ShouldBe(1, "the tampered group's merge cannot prove");
        var record = LoggedRebudgetRecords().ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Information,
            "448 is documented at Information — a Debug downgrade hides the line from default logs");
        record.Message.ShouldBe(
            "Chunk-budget rebudget at 1022 tokens: 1 note group(s) and 0 mirror group(s) re-chunked, "
            + "0 unchanged, 1 retryable, 0 unprovable, 0 terminal in 00:05:05.5000000",
            "with a nonzero retryable count, swapping the retryable/unprovable arguments changes this line");
    }

    /// <summary>Delegates to the fixture's counting service, but advances the phase clock once per
    /// budget resolution — the embedding-service call <see cref="ChunkPositionScanner.BudgetAsync" />
    /// makes while it scans — so a test can observe where the elapsed bracket starts.</summary>
    private sealed class BudgetScanAdvancingEmbeddingService(
        CountingEmbeddingService inner, FakeTimeProvider clock, TimeSpan scanSpan) : IEmbeddingService
    {
        public string EngineFingerprint(string provider, string? model, string? baseUrl) =>
            inner.EngineFingerprint(provider, model, baseUrl);

        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings) =>
            inner.CreateGenerator(settings);

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) =>
            inner.TrimQueryToWindow(settings, query);

        public string DocumentText(EmbeddingSettings settings, string text) => inner.DocumentText(settings, text);

        public double? RelevanceFloor(EmbeddingSettings settings) => inner.RelevanceFloor(settings);

        public int ResolveChunkBudgetFor(EmbeddingSettings settings)
        {
            var budget = inner.ResolveChunkBudgetFor(settings);
            clock.Advance(scanSpan);
            return budget;
        }

        public int ResolveDimensions(EmbeddingSettings settings) => inner.ResolveDimensions(settings);

        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) => inner.ResolveTokenizer(settings);
    }

    private sealed record Row(long Id, string Hash, string Value, string EmbedState, string? AgentId,
        string? SourceFile, string? Section, long? SourceId, long CreatedAt, long ChunkIndex, long TotalChunks,
        long? TtlDays);

    private sealed record BucketRow(long Id, string Hash, string Value, string? Scope, string? ContextLabel,
        string? WorkspaceId, string EmbedState);
}
