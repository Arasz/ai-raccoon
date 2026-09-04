using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Reqnroll;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.BDD;

// Steps for docs/work/features-single-project-id/single-project-id.feature (air-merge P-INT):
// the P1 census + P2 repair + P3/P4 enforcement working together. Seeding is raw SQL over the
// scenario bank (the same seam the P2/P-INT integration tests use); the repair is the real
// ProjectIdsRepairJob; the scan leg reuses the file-watcher step vocabulary and tick.
[Binding]
public sealed class SingleProjectIdSteps(ScenarioContext scenarioContext)
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private const string RaccoonWinner = "ai-raccoon";
    private const string RaccoonLoser = "AI-RACCOON";

    private readonly MemoryFeatureContext _ctx = scenarioContext.ScenarioContainer.Resolve<MemoryFeatureContext>();
    private readonly FileWatcherFeatureContext _watch = scenarioContext.ScenarioContainer.Resolve<FileWatcherFeatureContext>();

    private static string FixtureMapJson() =>
        new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
            ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
            ["qa-noise-project", "manual-sweep"]).ToJson();

    [Given("^a bank with the jsaa split cluster and the AI-RACCOON casing split$")]


    public async Task GivenSplitClusterBank()
    {
        var ct = TestContext.Current.CancellationToken; // QA F3: fail the step, not the run timeout.
        await using var connection = await _ctx.OpenBankAsync(ct);
        var now = MemoryFeatureContext.FixedNow.ToUnixTimeSeconds();

        async Task Entry(string hash, string projectId, string? label)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @hash, 'seed.md', 's', 'project', @projectId, @label, @now, @now, 'pending')",
                new { hash, projectId, label, now }, cancellationToken: ct));
        }

        async Task Queue(string projectId, string hash)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) " +
                "VALUES (@projectId, @hash, @hash, 0.5, @now, @now)",
                new { projectId, hash, now }, cancellationToken: ct));
        }

        await Entry("w1", Winner, "ctx-a");
        await Entry("w2", Winner, "ctx-a");
        await Entry("l1", Loser, "ctx-a");
        await Entry("l2", Loser, "ctx-a");
        await Queue(Winner, "q-w1");
        await Queue(Winner, "q-w2");
        await Queue(Loser, "q-l1");
        await Queue(Loser, "q-l2");
        await Entry("r1", RaccoonWinner, "ctx-a");
        await Entry("r2", RaccoonWinner, "ctx-a");
        await Entry("R1", RaccoonLoser, "ctx-a");
        await Entry("R2", RaccoonLoser, "ctx-a");
    }

    [When("^the project-ids repair runs$")]
    public async Task WhenRepairRuns()
    {
        var ct = TestContext.Current.CancellationToken; // QA F3: fail the step, not the run timeout.
        await using var connection = await _ctx.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = MemoryFeatureContext.FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() },
            cancellationToken: ct));
        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), _ctx.TimeProvider);
        (await job.RunAsync(connection, ct)).ShouldBeTrue("the split-cluster bank must fold");
        // FileWatcherSteps precedent: model elapsed time after the repair — a same-fake-second
        // post-repair edit could tie the pre-repair watermark and be skipped as not-due.
        // Production never ties; fake time must not either.
        _ctx.TimeProvider.Advance(TimeSpan.FromSeconds(30));
    }

    // Ledger — skip-entries-rewrite : "Loser rows meet under the winner" scenario : jsaa 2+2 +
    // AI-RACCOON 2+2 labeled rows (an entries-only rewrite still leaves loser rows behind);
    // skip-projects-ensure : same : winners-registered assert (loser registry row must delete).
    [Then("^no labeled entries row is loser-keyed$")]
    public async Task ThenNoLabeledEntriesLoserKeyed()
    {
        await using var connection = await _ctx.OpenBankAsync(CancellationToken.None);
        (await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM entries WHERE project_id IN ('job-search-ai-assistant', 'AI-RACCOON') " +
                "AND scope = 'project' AND context_label IS NOT NULL"))
            .ShouldBe(0, "every foldable loser row meets under its winner");
    }

    [Then("^the jsaa and ai-raccoon winners are registered$")]
    public async Task ThenWinnersRegistered()
    {
        await using var connection = await _ctx.OpenBankAsync(CancellationToken.None);
        var ids = (await connection.QueryAsync<string>("SELECT id FROM projects")).ToList();
        ids.ShouldContain(Winner);
        ids.ShouldContain(RaccoonWinner);
        ids.ShouldNotContain(Loser, "the loser registry row never survives the fold");
    }

    // Ledger — skip-queue-rewrite : "The split queue meets under the winner" scenario : jsaa 2 +
    // loser 2 queued rows (the winner holds every row only when the queue fold ran).
    [Then("^the winner's queue holds every row$")]
    public async Task ThenWinnerQueueHoldsEveryRow()
    {
        await using var connection = await _ctx.OpenBankAsync(CancellationToken.None);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM promotion_queue WHERE project_id = 'jsaa'"))
            .ShouldBe(4, "2 winner + 2 folded loser rows meet under the winner");
    }

    [Then("^no queue row is loser-keyed$")]
    public async Task ThenNoQueueRowLoserKeyed()
    {
        await using var connection = await _ctx.OpenBankAsync(CancellationToken.None);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM promotion_queue WHERE project_id = 'job-search-ai-assistant'"))
            .ShouldBe(0, "the loser queue key is absent after the fold");
    }

    [When("^a new file \"([^\"]*)\" appears under the watched path$")]
    public void WhenNewFileAppears(string file)
    {
        var path = Path.Combine(_watch.MapPath("/repo"), file);
        _watch.WriteFile(path, $"{file} post-repair scan content");
    }

    // Ledger — scan-resurrects-loser : "Scan after repair keeps the winner key" scenario : loser
    // watch + ingested notes-a/b, post-repair notes-c + tick (a scan storing under the watch's
    // pre-repair id reintroduces the loser key on every surface asserted below).
    [Then("^no watch, file, digest or queue row is loser-keyed$")]
    public async Task ThenNoWatchSurfaceLoserKeyed()
    {
        await using var connection = await _ctx.OpenBankAsync(CancellationToken.None);
        foreach (var table in new[] { "watches", "watch_files", "watch_digest_claims", "promotion_queue" })
        {
            // Table names are this step's own constants, never bank content.
            (await connection.ExecuteScalarAsync<long>(
                    $"SELECT count(*) FROM {table} WHERE project_id = 'job-search-ai-assistant'"))
                .ShouldBe(0, $"a post-repair scan must not resurrect the loser key in {table}");
        }
    }

    [Then("^\"([^\"]*)\" is watched under \"([^\"]*)\"$")]
    public async Task ThenPathWatchedUnder(string virtualPath, string projectId)
    {
        await using var connection = await _ctx.OpenBankAsync(CancellationToken.None);
        var owners = (await connection.QueryAsync<string>(
                new CommandDefinition("SELECT DISTINCT project_id FROM watches WHERE path = @path",
                    new { path = _watch.MapPath(virtualPath) }, cancellationToken: CancellationToken.None)))
            .ToList();
        owners.ShouldBe([projectId], "the watch renames to the winner and stays there across ticks");
    }

    [Then("^the new file \"([^\"]*)\" is ingested under \"([^\"]*)\"$")]
    public async Task ThenNewFileIngestedUnder(string file, string projectId)
    {
        var realPath = Path.Combine(_watch.MapPath("/repo"), file);
        await _watch.ReconcileOnceAsync();
        var ingested = await _watch.StepUntilAsync(async () =>
        {
            await using var connection = await _ctx.OpenBankAsync(CancellationToken.None);
            var owner = await connection.ExecuteScalarAsync<string>(
                new CommandDefinition("SELECT project_id FROM entries WHERE source_file = @path LIMIT 1",
                    new { path = realPath }, cancellationToken: CancellationToken.None));
            return string.Equals(owner, projectId, StringComparison.Ordinal);
        }, maxFakeSeconds: 10);

        ingested.ShouldBeTrue(
            "the post-repair ticks ingest the new file under the winner — the scan never resurrects the loser");
        await using var verify = await _ctx.OpenBankAsync(CancellationToken.None);
        (await verify.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT count(*) FROM entries WHERE source_file = @path AND project_id = 'job-search-ai-assistant'",
                    new { path = realPath }, cancellationToken: CancellationToken.None)))
            .ShouldBe(0, "no loser-keyed row for the new file");
    }
}
