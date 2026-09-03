using System.Text.Json;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>Air-merge P1: reproducible orphan/fragment census over a seeded bank mirroring the research-record clusters (jsaa split queue+code, ai-badger zero-entry guids, hermes-default quality rows, ai-raccoon casing split, drop candidates). Seeded banks only — never the live bank.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdCensusTests
{
    private const string GuidJsaa = "01a062f4-0000-7000-8000-000000000001";
    private const string GuidBadgerA = "01a03024-0000-7000-8000-000000000001";
    private const string GuidBadgerB = "01a0302f-0000-7000-8000-000000000002";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     Per populated surface: every id-keyed counter lands on its id's row.
    ///     Ledger — drop-a-surface-count : --filter Collect_OnASeededMultiClusterBank_ReportsEveryIdKeyedSurfacePerId :
    ///     multi-cluster bank (queue+code+quality+watches+tombstones+metrics+settings seeded).
    /// </summary>
    [RetryFact]
    public async Task Collect_OnASeededMultiClusterBank_ReportsEveryIdKeyedSurfacePerId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenSeededAsync(ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);

        var jsaa = report.Row("jsaa");
        jsaa.ProjectEntries.ShouldBe(2);
        jsaa.CustomEntries.ShouldBe(1);
        jsaa.SharedEntries.ShouldBe(1);
        jsaa.WorkspaceEntries.ShouldBe(1, "the workspace scratch row carries the id but is never committed");
        jsaa.NullContextEntries.ShouldBe(2, "one bulk row plus the workspace row");
        jsaa.Queued.ShouldBe(2);
        jsaa.Discards.ShouldBe(1);
        jsaa.WatchFiles.ShouldBe(1);
        jsaa.DigestClaims.ShouldBe(1);
        jsaa.Tombstones.ShouldBe(1);
        jsaa.Workspaces.ShouldBe(1);
        jsaa.MetricsRows.ShouldBe(1);
        jsaa.CodeEntries.ShouldBe(1);
        jsaa.VecCodeRows.ShouldBe(1);
        jsaa.CodeFtsRows.ShouldBe(1);
        jsaa.VecEntryRows.ShouldBe(1);
        jsaa.Registered.ShouldBeFalse();
        jsaa.Orphan.ShouldBeTrue();
        jsaa.SettingsKeys.ShouldContain("access.mode.project:jsaa");

        var loser = report.Row("job-search-ai-assistant");
        loser.ProjectEntries.ShouldBe(1);
        loser.Queued.ShouldBe(1);
        loser.CodeEntries.ShouldBe(1);
        loser.VecCodeRows.ShouldBe(0, "the loser code row stays pending, so no vec_code row exists for it");
        loser.Orphan.ShouldBeTrue();

        var guid = report.Row(GuidJsaa);
        guid.EntryTotal.ShouldBe(2);
        guid.Registered.ShouldBeTrue();
        guid.RegisteredName.ShouldBe("job-search-ai-assistant");
        guid.Orphan.ShouldBeFalse();
    }

    /// <summary>
    ///     Retire/delete candidates surface with their attachments before any decision.
    ///     Ledger — skip-zero-entry-rows : --filter Collect_SurfacesZeroEntryGuids_WithTheirAttachments :
    ///     two zero-entry guids + drop candidates with watch/quality/noise attachments.
    /// </summary>
    [RetryFact]
    public async Task Collect_SurfacesZeroEntryGuids_WithTheirAttachments()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenSeededAsync(ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);

        var retired = report.ZeroEntryRows.Select(r => r.ProjectId).Order(StringComparer.Ordinal).ToList();
        retired.ShouldContain(GuidBadgerA);
        retired.ShouldContain(GuidBadgerB);
        report.Row(GuidBadgerA).RegisteredName.ShouldBe("ai-badger");

        var badger = report.Row("ai-badger");
        badger.EntryTotal.ShouldBe(2);
        badger.NoiseRows.ShouldBe(1);
        badger.Orphan.ShouldBeTrue();

        var hermes = report.Row("hermes-default");
        hermes.QualityRows.ShouldBe(2, "the search_quality key-column leg must attribute hermes-default rows");
        hermes.Orphan.ShouldBeTrue();

        var sweep = report.Row("manual-sweep");
        sweep.Watches.ShouldBe(1, "a drop candidate's attachments must be visible before any delete decision");

        var noise = report.Row("qa-noise-project");
        noise.EntryTotal.ShouldBe(1);
        noise.AttachmentCount.ShouldBe(0);
    }

    /// <summary>
    ///     Id-embedding keys attribute to their owner; global keys stay unattributed.
    ///     Ledger — skip-key-attribution : --filter Collect_AttributesSettingsKeys_ById_LeavingGlobalsUnattributed :
    ///     colliding ingest.scope casing pair + watch.enabled loser key + two globals.
    /// </summary>
    [RetryFact]
    public async Task Collect_AttributesSettingsKeys_ById_LeavingGlobalsUnattributed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenSeededAsync(ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);

        report.Row("ai-raccoon").SettingsKeys.ShouldBe(["ingest.scope.ai-raccoon"]);
        report.Row("AI-RACCOON").SettingsKeys.ShouldContain("ingest.scope.AI-RACCOON");
        report.Row("AI-RACCOON").SettingsKeys.ShouldContain("watch.enabled.AI-RACCOON");
        report.UnattributedSettingsKeys.ShouldContain("ingest.scope.global");
        report.UnattributedSettingsKeys.ShouldContain("sync.provider");
    }

    /// <summary>
    ///     NULL-scope/NULL-context rows are counted bank-wide, not attributed.
    ///     Ledger — miscount-nulls : --filter Collect_CountsNullScopeAndNullContextRows_BankWide :
    ///     1 NULL-scope + 2 NULL-context rows (a miscount hides bulk residue).
    /// </summary>
    [RetryFact]
    public async Task Collect_CountsNullScopeAndNullContextRows_BankWide()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenSeededAsync(ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);

        report.NullScopeEntries.ShouldBe(1, "the workspace-scoped row carries NULL scope");
        report.NullContextEntries.ShouldBe(2, "one jsaa bulk row plus the workspace row carry NULL context_label");
    }

    /// <summary>
    ///     The read-only proof: under PRAGMA query_only the census returns EXACTLY the full report
    ///     a normal run returns — compared as JSON over twin seeded banks (FixedNow makes the seeds
    ///     byte-identical), not just a non-empty smoke. Record Equals cannot serve here: the row
    ///     and key properties are lists, which compare by reference. Any write attempt throws
    ///     under query_only.
    ///     Ledger — write-under-census : --filter Collect_RunsUnderQueryOnly_ProvingZeroBankWrites :
    ///     twin seeded banks compared by full-report JSON equality.
    /// </summary>
    [RetryFact]
    public async Task Collect_RunsUnderQueryOnly_ProvingZeroBankWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenSeededAsync(ct);
        await using var baseline = await OpenSeededAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA query_only = ON", cancellationToken: ct));

        var report = await ProjectIdCensus.CollectAsync(connection, ct);
        var expected = await ProjectIdCensus.CollectAsync(baseline, ct);

        JsonSerializer.Serialize(report).ShouldBe(JsonSerializer.Serialize(expected));
    }

    /// <summary>
    ///     Empty bank, empty report — no phantom rows, no unattributed keys.
    ///     Ledger — phantom-rows : --filter Collect_OnAnEmptyBank_ReturnsNoRowsAndZeroCounters :
    ///     schema-only bank (any row at all reddens).
    /// </summary>
    [RetryFact]
    public async Task Collect_OnAnEmptyBank_ReturnsNoRowsAndZeroCounters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(ct);
        await MemorySchema.EnsureAsync(connection, ct);

        var report = await ProjectIdCensus.CollectAsync(connection, ct);

        report.Rows.ShouldBeEmpty();
        report.NullScopeEntries.ShouldBe(0);
        report.NullContextEntries.ShouldBe(0);
        report.UnattributedSettingsKeys.ShouldBeEmpty();
    }

    private static async Task<SqliteConnection> OpenSeededAsync(CancellationToken ct)
    {
        var connection = await OpenAsync(ct);
        await MemorySchema.EnsureAsync(connection, ct);
        await SeedAsync(connection, ct);
        return connection;
    }

    private static async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static async Task SeedAsync(SqliteConnection connection, CancellationToken ct)
    {
        var now = FixedNow.ToUnixTimeSeconds();
        var contentVector = EmbeddingBlob.ToBytes(new float[384]);
        var codeVector = EmbeddingBlob.ToBytes(new float[768]);

        async Task Entry(string hash, string scope, string projectId, string? label)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @hash, 'seed.md', 's', @scope, @projectId, @label, @now, @now, 'pending')",
                new { hash, scope, projectId, label, now }, cancellationToken: ct));
        }

        // jsaa cluster: orphan canonical (2 project incl. 1 NULL-ctx + 1 custom + 1 shared-scope row), split queue, split code.
        await Entry("jsaa-1", "project", "jsaa", "ctx-a");
        await Entry("jsaa-2", "project", "jsaa", null);
        await Entry("jsaa-3", "custom", "jsaa", "ctx-a");
        await Entry("jsaa-4", "shared", "jsaa", "ctx-a");
        await Queue("jsaa", "jsaa-q1", 0.9);
        await Queue("jsaa", "jsaa-q2", 0.8);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO promotion_discards (project_id, hash, discarded_at) VALUES ('jsaa', 'jsaa-no', @now)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES ('jsaa', 'jsaa-gone', 'project', @now)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', 'jsaa', 'open', @now)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at, embed_state) " +
            "VALUES ('jsaa-ws', 'jsaa-ws', 'jsaa-ws', 'seed.md', 's', NULL, 'jsaa', NULL, 'ws-1', @now, @now, 'pending')",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('jsaa', '/repo/a.cs', 'h', @now)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watch_digest_claims (project_id, path, claimed_at) VALUES ('jsaa', '/repo/a.cs', @now)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO metrics (name, kind, value, unit, project_id, recorded_at) VALUES ('m', 'k', 1, 'u', 'jsaa', @now)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO metrics (name, kind, value, unit, project_id, recorded_at) VALUES ('m', 'k', 1, 'u', NULL, @now)",
            new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES ('access.mode.project:jsaa', 'full')", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embedding = @embedding, embed_state = 'embedded' WHERE hash = 'jsaa-1'",
            new { embedding = contentVector }, cancellationToken: ct));
        await Code(connection, "jsaa", "code-jsaa", true, codeVector, now, ct);

        // job-search-ai-assistant loser: entries + queue + pending code, unregistered.
        await Entry("loser-1", "project", "job-search-ai-assistant", "ctx-a");
        await Queue("job-search-ai-assistant", "loser-q1", 0.7);
        await Code(connection, "job-search-ai-assistant", "code-loser", false, codeVector, now, ct);

        // Guid loser standing in for 01a062f4: registered under the loser name, 2 entries.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO projects (id, name, created_at) VALUES (@id, 'job-search-ai-assistant', @now)",
            new { id = GuidJsaa, now }, cancellationToken: ct));
        await Entry("guid-1", "project", GuidJsaa, "ctx-a");
        await Entry("guid-2", "project", GuidJsaa, "ctx-a");

        // ai-badger: orphan verbatim + two zero-entry guid rows (retire candidates).
        await Entry("badger-1", "project", "ai-badger", "ctx-a");
        await Entry("badger-2", "project", "ai-badger", "ctx-a");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO projects (id, name, created_at) VALUES (@id, 'ai-badger', @now)",
            new { id = GuidBadgerA, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO projects (id, name, created_at) VALUES (@id, NULL, @now)",
            new { id = GuidBadgerB, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO noise_entries (request_content, project_id, detected_by_policy, expires_at, created_at) " +
            "VALUES ('junk', 'ai-badger', 'p', @now, @now)",
            new { now }, cancellationToken: ct));

        // hermes-default: orphan verbatim owning search_quality rows.
        await Entry("hermes-1", "project", "hermes-default", "ctx-a");
        await Quality(connection, "q-1", "hermes-default", now, ct);
        await Quality(connection, "q-2", "hermes-default", now, ct);
        await Quality(connection, "q-3", null, now, ct);

        // ai-raccoon casing split: entries collapse, settings keys do not.
        await Entry("raccoon-1", "project", "ai-raccoon", "ctx-a");
        await Entry("RACCOON-1", "project", "AI-RACCOON", "ctx-a");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings (key, value) VALUES ('ingest.scope.ai-raccoon', '[]'), ('ingest.scope.AI-RACCOON', '[]'), " +
            "('watch.enabled.AI-RACCOON', 'true'), ('ingest.scope.global', '[]'), ('sync.provider', 'none')",
            cancellationToken: ct));

        // Drop candidates: manual-sweep owns a watch (attachment), qa-noise-project owns nothing.
        await Entry("sweep-1", "project", "manual-sweep", "ctx-a");
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES ('manual-sweep', '/repo', @now, @now)",
            new { now }, cancellationToken: ct));
        await Entry("noise-1", "project", "qa-noise-project", "ctx-a");

        // Single-fragment verbatim: registered canonical, one entry.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO projects (id, name, created_at) VALUES ('deepseek-harness', 'deepseek-harness', @now)",
            new { now }, cancellationToken: ct));
        await Entry("deep-1", "project", "deepseek-harness", "ctx-a");

        async Task Queue(string projectId, string hash, double score)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) " +
                "VALUES (@projectId, @hash, @hash, @score, @now, @now)",
                new { projectId, hash, score, now }, cancellationToken: ct));
        }
    }

    private static async Task Code(SqliteConnection connection, string projectId, string hash, bool embedded, byte[] codeVector, long now, CancellationToken ct)
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

    private static async Task Quality(SqliteConnection connection, string correlationId, string? projectId, long now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO search_quality (correlation_id, query, project_id, created_at) VALUES (@correlationId, 'q', @projectId, @now)",
            new { correlationId, projectId, now }, cancellationToken: ct));
    }
}
