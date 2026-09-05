using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Air-merge P-INT regression gate: P1 (census) + P2 (repair vehicle) + P3/P4 (enforcement +
///     boundary fold) working together. A seeded multi-cluster bank mirroring the research-record
///     folds (jsaa 3-id cluster, AI-RACCOON casing split, qa-noise/manual-sweep drops, one retired
///     zero-row guid) goes through the real <see cref="ProjectIdsRepairJob" />; afterwards the
///     P1 orphan census reports zero orphans and the never-migrated partitions (NULL-context bulk
///     rows, NULL-scope rows, typo control excluded from this bank by design — see remarks) are
///     byte-identical. Seeded banks only — never the live bank.
///     <para>
///         The typo control and unmapped populated ids (hermes-default, single-fragment verbatims)
///         are deliberately ABSENT here: <c>Orphan = !Registered &amp;&amp; EntryTotal &gt; 0</c> and
///         the repair only registers fold winners, so an unmapped populated id stays orphan by
///         definition. Typo byte-identity is pinned by P2's Repair_FirstRunMovesRows_SecondRunNoOp;
///         hermes-default quality ownership by P1's census; live-bank counts (48,901 NULL-context,
///         28 NULL-scope, 1,214 quality join, roundtrip/sheepdog single rows) are labeled manual in
///         the P-INT report — seeded analogues are asserted here, never silently.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
// Running the real job reloads the process-wide alias cache (Package E1's job leg), so this class
// serializes with every other Default reader and puts the cache back on the way out.
[Collection(AiRaccoon.Tests.Unit.Projects.ProjectIdAliasDefaultCollection.Name)]
public sealed class SingleProjectIdCensusTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private const string GuidLoser = "01a062f4-0000-7000-8000-000000000001";
    private const string RaccoonWinner = "ai-raccoon";
    private const string RaccoonLoser = "AI-RACCOON";
    private const string DroppedQa = "qa-noise-project";
    private const string DroppedSweep = "manual-sweep";
    private const string RetiredGuid = "01a03024-0000-7000-8000-000000000001";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("single-project-id-census");
    private readonly SqliteConnectionFactory _factory;

    public SingleProjectIdCensusTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose()
    {
        ProjectIdAliasMap.ResetDefault();
        TestData.DeleteTempRoot(_dataRoot);
    }

    public static TheoryData<string, string[]> PopulatedClusters() => new()
    {
        { Winner, [Loser, GuidLoser] },
        { RaccoonWinner, [RaccoonLoser] },
    };

    /// <summary>
    ///     Per populated cluster: after the real repair, the winner is registered and non-orphan
    ///     while every loser id owns zero entries on every surface. Zero-row folds are excluded
    ///     from the claim by construction (the theory only names folds with &gt;0 rows).
    ///     Ledger — revert-jsaa-fold : --filter "FullyQualifiedName~SingleProjectIdCensusTests.FoldedCluster_HasNoOrphans" :
    ///     full multi-cluster bank (jsaa 2+2+2 rows, raccoon 2+2 rows, drops); revert-AI-RACCOON-fold :
    ///     same filter : same bank — each revert reddens only its own cluster case.
    /// </summary>
    [RetryTheory]
    [MemberData(nameof(PopulatedClusters))]
    public async Task FoldedCluster_HasNoOrphans(string winner, string[] losers)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedAsync(connection, ct);
        await RequestAndRunRepairAsync(connection, ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);

        var winnerRow = report.Row(winner);
        winnerRow.Registered.ShouldBeTrue();
        winnerRow.Orphan.ShouldBeFalse();
        foreach (var loser in losers)
        {
            report.Rows.Where(r => r.ProjectId == loser).ShouldBeEmpty($"{loser} folds into {winner}");
        }
    }

    /// <summary>
    ///     Global zero: no orphan row anywhere after the repair (mapped clusters fold, drops delete,
    ///     the retired guid retires). Ledger — revert-jsaa-fold :
    ///     --filter "FullyQualifiedName~SingleProjectIdCensusTests.OrphanCensus_ZeroAcrossEverySurface" :
    ///     full multi-cluster bank; drop-the-dropped-delete : same filter : same bank (orphan drops resurface).
    /// </summary>
    [RetryFact]
    public async Task OrphanCensus_ZeroAcrossEverySurface()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedAsync(connection, ct);
        await RequestAndRunRepairAsync(connection, ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);

        report.Rows.Where(r => r.Orphan).ShouldBeEmpty("every populated id is either a registered winner or deleted");
    }

    /// <summary>
    ///     Drop candidates delete with tombstones, never fold: zero rows on every surface, one
    ///     tombstone per removed entry-hash. Ledger — skip-dropped-delete :
    ///     --filter "FullyQualifiedName~SingleProjectIdCensusTests.DropCandidates_GoneWithTombstones" :
    ///     qa-noise 1 entry + manual-sweep 1 entry + sweep watch.
    /// </summary>
    [RetryFact]
    public async Task DropCandidates_GoneWithTombstones()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedAsync(connection, ct);
        await RequestAndRunRepairAsync(connection, ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);
        foreach (var id in new[] { DroppedQa, DroppedSweep })
        {
            var row = report.Row(id);
            row.EntryTotal.ShouldBe(0, $"{id} deletes — no entries survive");
            row.Orphan.ShouldBeFalse("a zero-entry id is not an orphan");
            row.AttachmentCount.ShouldBe(1, $"{id} keeps exactly its tombstone leg");
            row.Tombstones.ShouldBe(1);
        }
        (await ScalarAsync(connection, "SELECT count(*) FROM entries WHERE project_id IN ('qa-noise-project', 'manual-sweep')", ct))
            .ShouldBe(0);
        (await ScalarAsync(connection, "SELECT count(*) FROM watches WHERE project_id = 'manual-sweep'", ct))
            .ShouldBe(0, "the sweep watch deletes with its id");
        (await ScalarAsync(connection, "SELECT count(*) FROM sync_tombstones WHERE project_id IN ('qa-noise-project', 'manual-sweep')", ct))
            .ShouldBe(2, "one tombstone per genuinely removed entry hash");
        (await ScalarAsync(connection, "SELECT count(*) FROM projects WHERE id IN ('qa-noise-project', 'manual-sweep', @retired)", ct, new { retired = RetiredGuid }))
            .ShouldBe(0, "dropped and retired ids leave no registry rows");    }

    /// <summary>
    ///     D1 committed predicate: winner NULL-context bulk rows and NULL-scope workspace scratch survive
    ///     the repair under their original keys, while LOSER NULL-context bulk rows and custom rows fold
    ///     to the winner (the d-426 keep is overturned). Ledger — widen-the-rewrite-predicate :
    ///     --filter "FullyQualifiedName~SingleProjectIdCensusTests.CommittedNullAndCustom_FoldWhileWinnerBulkAndScratchStay" :
    ///     winner bulk + workspace scratch (stay) + loser bulk + loser custom (fold).
    /// </summary>
    [RetryFact]
    public async Task CommittedNullAndCustom_FoldWhileWinnerBulkAndScratchStay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedAsync(connection, ct);
        // d-427 SHOULD-6 inverted by D1: the loser keep-leg now folds, so it is seeded here —
        // a loser NULL-ctx row and a loser custom row join the winner bulk + scratch fixtures.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
            "VALUES ('l-bulk', 'l-bulk', 'l-bulk', 'seed.md', 's', 'project', @loser, NULL, 1, 1, 'pending'), " +
            "('l-custom', 'l-custom', 'l-custom', 'seed.md', 's', 'custom', @loser, 'ctx-a', 1, 1, 'pending')",
            new { loser = Loser }, cancellationToken: ct));
        var before = await ProjectIdCensus.CollectAsync(connection, ct);

        await RequestAndRunRepairAsync(connection, ct);

        var after = await ProjectIdCensus.CollectAsync(connection, ct);
        after.NullContextEntries.ShouldBe(before.NullContextEntries);
        after.NullContextEntries.ShouldBe(3, "bulk rows re-key, they never vanish: winner bulk + scratch + folded loser bulk");
        after.NullScopeEntries.ShouldBe(before.NullScopeEntries);
        after.NullScopeEntries.ShouldBe(1, "the NULL-scope workspace scratch row is counted, not touched");
        (await ScalarAsync(connection, "SELECT project_id FROM entries WHERE hash = 'w-bulk'", ct))
            .ShouldBe(Winner);
        (await ScalarAsync(connection, "SELECT project_id FROM entries WHERE hash = 'ws-1'", ct))
            .ShouldBe(Winner);
        (await ScalarAsync(connection, "SELECT project_id FROM entries WHERE hash = 'l-bulk'", ct))
            .ShouldBe(Winner, "D1: the loser NULL-context bulk row folds to the winner");
        (await ScalarAsync(connection, "SELECT project_id FROM entries WHERE hash = 'l-custom'", ct))
            .ShouldBe(Winner, "D1: the loser custom row folds to the winner");
        (await ScalarAsync(connection, "SELECT scope FROM entries WHERE hash = 'l-custom'", ct))
            .ShouldBe("custom", "the fold re-keys the id — the scope rides along untouched");
        (await ScalarAsync(connection, "SELECT count(*) FROM entries WHERE project_id = @loser", ct, new { loser = Loser }))
            .ShouldBe(0, "no loser entry row survives on any committed scope");
    }

    /// <summary>
    ///     Seeded analogue of the hermes-default quality-join gate: quality rows fold with their id
    ///     and the total is preserved (live 1,214-row every-kind join is labeled manual — a seeded
    ///     bank cannot reproduce live drift). Ledger — skip-quality-fold :
    ///     --filter "FullyQualifiedName~SingleProjectIdCensusTests.QualityRows_FoldedAndPreserved" :
    ///     winner + loser + NULL-project quality rows.
    /// </summary>
    [RetryFact]
    public async Task QualityRows_FoldedAndPreserved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedAsync(connection, ct);
        var before = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM search_quality");

        await RequestAndRunRepairAsync(connection, ct);

        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM search_quality"))
            .ShouldBe(before, "quality rows fold or delete with their id — none appear or vanish");
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM search_quality WHERE project_id = @winner", new { winner = Winner }))
            .ShouldBe(2, "the loser quality row folds under the winner beside the winner's own");
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM search_quality WHERE project_id IS NULL"))
            .ShouldBe(1, "the unattributed quality row stays unattributed");
        var report = await ProjectIdCensus.CollectAsync(connection, ct);
        report.Row(Winner).QualityRows.ShouldBe(2);
    }

    /// <summary>
    ///     Code + watch surfaces fold with their id: code rows meet under the winner, watches rename
    ///     as UPDATEs preserving the scan lease (secondary observable), digest claims follow.
    ///     Ledger — skip-code-rewrite :
    ///     --filter "FullyQualifiedName~SingleProjectIdCensusTests.CodeAndWatchSurfaces_Folded" :
    ///     winner + loser code rows, winner + loser watches with digest claims and a scan lease.
    /// </summary>
    [RetryFact]
    public async Task CodeAndWatchSurfaces_Folded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedAsync(connection, ct);
        await RequestAndRunRepairAsync(connection, ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);
        report.Row(Winner).CodeEntries.ShouldBe(2, "winner + folded loser code rows");
        report.Rows.Where(r => r.ProjectId == Loser && r.CodeEntries > 0).ShouldBeEmpty();
        report.Row(Winner).Watches.ShouldBe(2);
        report.Row(Winner).WatchFiles.ShouldBe(2);
        report.Row(Winner).DigestClaims.ShouldBe(2);
        (await ScalarAsync(connection, "SELECT scan_owner FROM watches WHERE project_id = 'jsaa' AND path = '/repo/b'", ct))
            .ShouldBe("owner-l", "the folded watch keeps its scan lease — renames are UPDATEs, never DELETE+INSERT");
    }

    /// <summary>
    ///     Settings keys fold per prefix with winner-values standing; keys that embed no id stay
    ///     untouched (neighbouring observable beyond the renamed rows). Ledger — skip-settings-rename :
    ///     --filter "FullyQualifiedName~SingleProjectIdCensusTests.SettingsKeys_FoldedGlobalsUntouched" :
    ///     colliding ingest.scope pair + watch.enabled loser key + one global key.
    /// </summary>
    [RetryFact]
    public async Task SettingsKeys_FoldedGlobalsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedAsync(connection, ct);
        await RequestAndRunRepairAsync(connection, ct);

        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'ingest.scope.jsaa'", ct))
            .ShouldBe("[\"/x\"]", "on a settings collision the winner's value stands");
        (await ScalarAsync(connection, "SELECT count(*) FROM settings WHERE key LIKE '%job-search-ai-assistant'", ct))
            .ShouldBe(0, "every id-embedding loser key renames");
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'watch.enabled.jsaa'", ct))
            .ShouldBe("true");
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'sync.provider'", ct))
            .ShouldBe("none", "keys embedding no id are never touched");
        var report = await ProjectIdCensus.CollectAsync(connection, ct);
        report.UnattributedSettingsKeys.ShouldContain("sync.provider");
    }


    private static string FixtureMapJson() =>
        new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
            ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
            ["qa-noise-project", "manual-sweep"]).ToJson();

    private async Task RequestAndRunRepairAsync(SqliteConnection connection, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() }, cancellationToken: ct));
        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));
        (await job.RunAsync(connection, ct)).ShouldBeTrue("the seeded bank owns loser rows on moveable surfaces");
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct, object? param = null) =>
        await connection.ExecuteScalarAsync<object?>(new CommandDefinition(sql, param, cancellationToken: ct));

    /// <summary>
    ///     Multi-cluster fixture (≥2 ids × ≥2 rows per populated cluster, both queue sides, watch +
    ///     digest rows, code rows, settings collisions, NULL-ctx/NULL-scope rows, drops, one retired
    ///     zero-row guid): every surface the census enumerates is populated, so an entries-only
    ///     rewrite still fails the suite.
    /// </summary>
    private static async Task SeedAsync(SqliteConnection connection, CancellationToken ct)
    {
        var now = FixedNow.ToUnixTimeSeconds();
        var codeVector = EmbeddingBlob.ToBytes(new float[768]);

        async Task Entry(string hash, string? scope, string projectId, string? label, string? workspace = null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @hash, 'seed.md', 's', @scope, @projectId, @label, @workspace, @now, @now, 'pending')",
                new { hash, scope, projectId, label, workspace, now }, cancellationToken: ct));
        }

        async Task Queue(string projectId, string hash, double score)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) " +
                "VALUES (@projectId, @hash, @hash, @score, @now, @now)",
                new { projectId, hash, score, now }, cancellationToken: ct));
        }

        async Task Code(string projectId, string hash, bool embedded)
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at, embed_state) " +
                "VALUES (@hash, 'a.cs', @hash, 'a.cs', 1, 9, @projectId, @now, @now, 'pending') RETURNING id",
                new { hash, projectId, now }, cancellationToken: ct));
            if (embedded)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE code_entries SET embedding = @embedding, embed_state = 'embedded' WHERE id = @id",
                    new { embedding = codeVector, id }, cancellationToken: ct));
            }
        }

        // jsaa 3-id cluster: winner rows + queue, loser rows + queue + pending code, guid rows.
        await Entry("w1", "project", Winner, "ctx-a");
        await Entry("w2", "project", Winner, "ctx-a");
        await Entry("w-bulk", "project", Winner, null);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', 'jsaa', 'open', @now)",
            new { now }, cancellationToken: ct));
        await Entry("ws-1", null, Winner, null, "ws-1");
        await Queue(Winner, "q-w1", 0.9);
        await Queue(Winner, "q-w2", 0.8);
        await Code(Winner, "c-w", true);
        await Entry("l1", "project", Loser, "ctx-a");
        await Entry("l2", "project", Loser, "ctx-a");
        await Queue(Loser, "q-l1", 0.7);
        await Queue(Loser, "q-l2", 0.6);
        await Code(Loser, "c-l", false);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO projects (id, name, created_at) VALUES (@id, @loser, @now)",
            new { id = GuidLoser, loser = Loser, now }, cancellationToken: ct));
        await Entry("g1", "project", GuidLoser, "ctx-a");
        await Entry("g2", "project", GuidLoser, "ctx-a");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES (@loser, 't-loser-gone', 'project', @now)",
            new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watches (project_id, path, created_at, last_change_ts, scan_owner, scan_lease_expires_at) " +
            "VALUES ('jsaa', '/repo/a', @now, @now, 'owner-w', 999)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watches (project_id, path, created_at, last_change_ts, scan_owner, scan_lease_expires_at) " +
            "VALUES (@loser, '/repo/b', @now, @now, 'owner-l', 999)",
            new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('jsaa', '/repo/a', 'h', @now), " +
            "(@loser, '/repo/b', 'h', @now)",
            new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watch_digest_claims (project_id, path, claimed_at) VALUES ('jsaa', '/repo/a', @now), " +
            "(@loser, '/repo/b', @now)",
            new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES ('ingest.scope.jsaa', '[\"/x\"]'), " +
            "('ingest.scope.job-search-ai-assistant', '[\"/y\"]'), ('watch.enabled.job-search-ai-assistant', 'true'), " +
            "('sync.provider', 'none')", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO search_quality (correlation_id, query, project_id, created_at) VALUES " +
            "('q-w', 'q', 'jsaa', @now), (@loser2, 'q', @loser, @now), ('q-null', 'q', NULL, @now)",
            new { loser = Loser, loser2 = "q-l", now }, cancellationToken: ct));

        // AI-RACCOON casing split: entries collapse, settings keys rename.
        await Entry("r1", "project", RaccoonWinner, "ctx-a");
        await Entry("r2", "project", RaccoonWinner, "ctx-a");
        await Entry("R1", "project", RaccoonLoser, "ctx-a");
        await Entry("R2", "project", RaccoonLoser, "ctx-a");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES ('ingest.scope.ai-raccoon', '[\"/r\"]'), " +
            "('ingest.scope.AI-RACCOON', '[\"/R\"]')", cancellationToken: ct));

        // Drop candidates + one retired zero-row guid.
        await Entry("n1", "project", DroppedQa, "ctx-a");
        await Entry("s1", "project", DroppedSweep, "ctx-a");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES (@sweep, '/repo', @now, @now)",
            new { sweep = DroppedSweep, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO projects (id, name, created_at) VALUES (@id, 'ai-badger', @now)",
            new { id = RetiredGuid, now }, cancellationToken: ct));
    }
}
