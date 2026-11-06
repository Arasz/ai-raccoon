using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Decisions;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

/// <summary>
///     Query, precedents and preview against an oracle computed in memory from the full export and the raw FTS hits,
///     the way the service computed them before it pushed filters into SQL. Every page of every filter must agree.
/// </summary>
public sealed partial class DecisionServiceTests
{
    private static readonly DateTimeOffset ParityNow = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [RetryFact]
    public async Task Query_pages_match_the_in_memory_oracle_for_every_filter()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await SeedParityProjectAsync(service, ct);
        var export = (await service.ExportAsync("a", new DecisionTaskSelector(all: true), ct)).Value;

        var selectors = new (DecisionTaskSelector? Selector, string[] Tasks)[]
        {
            (null, ["t1", "t2", "t3"]),
            (new DecisionTaskSelector(last: 1), ["t3"]),
            (new DecisionTaskSelector(taskIds: ["t1"]), ["t1"]),
            (new DecisionTaskSelector(taskIds: ["t2", "t3"]), ["t2", "t3"])
        };
        var checkedPages = 0;
        foreach (var (selector, tasks) in selectors)
        {
            foreach (var category in new[] { null, "arch" })
            {
                foreach (var statuses in new DecisionStatus[]?[] { null, [DecisionStatus.Accepted], [DecisionStatus.Proposed, DecisionStatus.Superseded] })
                {
                    foreach (var text in new[] { null, "alpha", "alpha shared", "nomatchword" })
                    {
                        foreach (var limit in new[] { 1, 4, 100 })
                        {
                            checkedPages += await AssertQueryParityAsync(service, export, selector, tasks, category, statuses, text, limit, ct);
                        }
                    }
                }
            }
        }

        checkedPages.ShouldBeGreaterThan(200);
    }

    [RetryFact]
    public async Task Precedents_match_the_in_memory_oracle()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await SeedParityProjectAsync(service, ct);
        var current = (await service.ExportAsync("a", new DecisionTaskSelector(all: true), ct)).Value.Decisions
            .Select(decision => decision.Versions.Single(version => version.Version == decision.Record.CurrentVersion))
            .ToDictionary(version => version.DecisionId, StringComparer.Ordinal);

        foreach (var scenario in new[] { "alpha", "shared", "alpha shared", "nomatchword" })
        {
            foreach (var category in new[] { null, "arch", "ops" })
            {
                foreach (var includeInactive in new[] { false, true })
                {
                    foreach (var limit in new[] { 1, 3, 20 })
                    {
                        var ranks = await RanksAsync(scenario, ct);
                        var expected = current.Values.Where(version => ranks.ContainsKey(version.DecisionId)
                                && (category is null || version.Content.Category == category)
                                && (includeInactive || version.Status == DecisionStatus.Accepted
                                    && (!version.Content.ValidFrom.HasValue || version.Content.ValidFrom.Value <= ParityNow)
                                    && (!version.Content.ValidUntil.HasValue || version.Content.ValidUntil.Value > ParityNow)))
                            .OrderBy(version => ranks[version.DecisionId]).ThenBy(version => version.DecisionId, StringComparer.Ordinal).ToArray();

                        var actual = (await service.PrecedentsAsync("a", scenario, category, limit, includeInactive, ct)).Value;

                        var label = $"{scenario}/{category}/{includeInactive}/{limit}";
                        actual.Decisions.Select(match => match.Decision.DecisionId).ShouldBe(expected.Take(limit).Select(version => version.DecisionId), label);
                        actual.Decisions.Select(match => match.Rank).ShouldBe(expected.Take(limit).Select(version => (double?)ranks[version.DecisionId]), label);
                        actual.TotalMatched.ShouldBe(expected.Length, label);
                        actual.HasMore.ShouldBe(expected.Length > limit, label);
                    }
                }
            }
        }
    }

    [RetryFact]
    public async Task Preview_counts_match_the_export_for_every_selector()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await SeedParityProjectAsync(service, ct);

        foreach (var selector in new[]
                 {
                     new DecisionTaskSelector(all: true), new DecisionTaskSelector(last: 1), new DecisionTaskSelector(taskIds: ["t1"]),
                     new DecisionTaskSelector(taskIds: ["t2", "t3"])
                 })
        {
            var export = (await service.ExportAsync("a", selector, ct)).Value;
            var preview = (await service.PreviewAsync("a", new DecisionPreviewOptions(selector, MaxDecisions: 100, MaxEdges: 200, MaxChars: 50000), ct)).Value;

            var live = export.Edges.Where(edge => edge.RemovedRevision is null).ToArray();
            var full = export.Decisions.Select(decision => decision.Record.DecisionId).ToHashSet(StringComparer.Ordinal);
            var boundary = live.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Where(id => !full.Contains(id)).Distinct(StringComparer.Ordinal).Count();
            preview.Total.ShouldBe(new DecisionPreviewCounts(export.Tasks.Count, export.Decisions.Count, boundary, live.Length));
            preview.Shown.Decisions.ShouldBe(export.Decisions.Count);
            preview.Total.Decisions.ShouldBeGreaterThan(0);
        }
    }

    private async Task<int> AssertQueryParityAsync(DecisionService service, DecisionExport export, DecisionTaskSelector? selector, string[] tasks,
        string? category, DecisionStatus[]? statuses, string? text, int limit, CancellationToken ct)
    {
        var taskSet = tasks.ToHashSet(StringComparer.Ordinal);
        var selected = export.Decisions.Where(decision => decision.Record.OriginTaskId is var origin && taskSet.Contains(origin)
                || decision.Versions.Any(version => taskSet.Contains(version.TaskId)))
            .Select(decision => decision.Record.DecisionId).ToHashSet(StringComparer.Ordinal);
        selected.UnionWith(export.Edges.Where(edge => taskSet.Contains(edge.TaskId) || edge.RemovalTaskId is { } removed && taskSet.Contains(removed))
            .Select(edge => edge.SourceId));
        var ranks = text is null ? null : await RanksAsync(text, ct);
        var expected = export.Decisions.Select(decision => decision.Versions.Single(version => version.Version == decision.Record.CurrentVersion))
            .Where(version => selected.Contains(version.DecisionId)
                && (category is null || version.Content.Category == category)
                && (statuses is null || statuses.Contains(version.Status))
                && (ranks is null || ranks.ContainsKey(version.DecisionId)))
            .OrderBy(version => version.DecisionId, StringComparer.Ordinal).ToArray();

        var pages = 0;
        string? after = null;
        var seen = new List<string>();
        while (true)
        {
            var label = $"{string.Join(',', tasks)}/{category}/{(statuses is null ? "-" : string.Join(',', statuses))}/{text}/{limit}/after {after}";
            var page = (await service.QueryAsync("a", new DecisionQuery(text, category, statuses, selector, limit, after), ct)).Value;
            var remaining = expected.Where(version => after is null || StringComparer.Ordinal.Compare(version.DecisionId, after) > 0).ToArray();
            page.Decisions.Select(match => match.Decision.DecisionId).ShouldBe(remaining.Take(limit).Select(version => version.DecisionId), label);
            page.Decisions.Select(match => match.Rank).ShouldBe(remaining.Take(limit).Select(version => ranks is null ? null : (double?)ranks[version.DecisionId]), label);
            page.Decisions.Select(match => match.Decision.Status).ShouldBe(remaining.Take(limit).Select(version => version.Status), label);
            page.TotalMatched.ShouldBe(expected.Length, label);
            page.HasMore.ShouldBe(remaining.Length > limit, label);
            page.Method.ShouldBe(text is null ? "exact" : "fts", label);
            seen.AddRange(page.Decisions.Select(match => match.Decision.DecisionId));
            pages++;
            if (!page.HasMore)
            {
                page.NextAfterId.ShouldBeNull(label);
                break;
            }

            page.NextAfterId.ShouldBe(page.Decisions[^1].Decision.DecisionId, label);
            after = page.NextAfterId;
        }

        seen.ShouldBe(expected.Select(version => version.DecisionId));
        return pages;
    }

    private async Task<Dictionary<string, double>> RanksAsync(string text, CancellationToken ct) =>
        (await ParityStore().ReadAsync("a", (reader, token) => reader.SearchAsync(text, token), ct))
        .ToDictionary(hit => hit.DecisionId, hit => hit.Rank, StringComparer.Ordinal);

    private SqliteDecisionStore ParityStore()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        return new SqliteDecisionStore(new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), new SharedProjectScope());
    }

    /// <summary>
    ///     Three tasks. t1 and t2 record twelve decisions each across two categories and statuses, some expired; t3 amends
    ///     and supersedes t1 decisions and links t1 sources, so t3 selects decisions only through history and edges.
    /// </summary>
    private static async Task SeedParityProjectAsync(DecisionService service, CancellationToken ct)
    {
        foreach (var task in new[] { "t1", "t2", "t3" })
        {
            await service.RegisterTaskAsync("a", new DecisionWrite(task, "s", "register-" + task), "Task " + task, cancellationToken: ct);
        }

        var ids = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["t1"] = [], ["t2"] = [] };
        for (var i = 0; i < 24; i++)
        {
            var task = i % 2 == 0 ? "t1" : "t2";
            var title = (i % 3 == 0 ? "alpha " : "beta ") + (i % 4 == 0 ? "shared " : string.Empty) + "decision " + i;
            var content = new DecisionContent(title, i % 5 < 3 ? "arch" : "ops", "scenario " + i, "outcome " + i, "reasoning " + i,
                validUntil: i % 7 == 0 ? ParityNow.AddDays(-1) : null, validFrom: i % 11 == 5 ? ParityNow.AddDays(1) : null);
            var status = i % 3 == 2 ? DecisionStatus.Proposed : DecisionStatus.Accepted;
            ids[task].Add((await service.RecordAsync("a", new DecisionWrite(task, "s", $"record-{i}"), content, status, ct)).Value.DecisionId);
        }

        var revision = (await service.ExportAsync("a", cancellationToken: ct)).ProjectRevision;
        revision = (await service.AmendAsync("a", new DecisionWrite("t3", "s", "amend-0"), ids["t1"][0], 1, "new evidence",
            new DecisionContent("alpha amended", "ops", "scenario", "outcome", "reasoning"), ct)).ProjectRevision;
        revision = (await service.SupersedeAsync("a", new DecisionWrite("t3", "s", "supersede"), ids["t1"][2], ids["t1"][3], 1, 1, "replaced", ct)).ProjectRevision;
        revision = (await service.LinkAsync("a", new DecisionWrite("t3", "s", "link-1"), ids["t1"][6], ids["t2"][0], DecisionEdgeKind.DependsOn, "needs", revision, ct)).ProjectRevision;
        var removable = await service.LinkAsync("a", new DecisionWrite("t1", "s", "link-2"), ids["t1"][8], ids["t2"][1], DecisionEdgeKind.Supports, "helps", revision, ct);
        await service.UnlinkAsync("a", new DecisionWrite("t3", "s", "unlink"), removable.Value.EdgeId, "obsolete", removable.ProjectRevision, ct);
    }
}
