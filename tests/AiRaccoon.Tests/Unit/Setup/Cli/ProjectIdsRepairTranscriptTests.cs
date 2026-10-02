using AiRaccoon.Core.Projects;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Whole-transcript pins for `repair project-ids`: every line, in order, plus the exit code, for
///     each path through the command — so a restructuring of the handler cannot drop, reorder or
///     reword a line that the substring tests elsewhere do not happen to name.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsRepairTranscriptTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"repair-transcript-{Guid.CreateVersion7():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
        {
            Directory.Delete(_dataRoot, recursive: true);
        }
    }

    public static TheoryData<string> Scenarios() =>
    [
        "dry-run-no-map", "dry-run-no-map-template-exists", "dry-run-with-map", "dry-run-converged",
        "queue-only-actionable", "queue-only-attention", "queue-only-converged", "apply-loop",
        "missing-map"
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Transcript_IsUnchanged(string scenario)
    {
        var actual = await RunScenarioAsync(scenario);

        var dump = Environment.GetEnvironmentVariable("REPAIR_TRANSCRIPT_DUMP");
        if (dump is not null)
        {
            Directory.CreateDirectory(dump);
            await File.WriteAllTextAsync(Path.Combine(dump, scenario + ".txt"), actual, TestContext.Current.CancellationToken);
        }

        actual.TrimEnd().ShouldBe(Expected[scenario].TrimEnd());
    }

    private async Task<string> RunScenarioAsync(string scenario)
    {
        Directory.CreateDirectory(_dataRoot);
        var mapPath = WriteMap();
        string[] argv = scenario switch
        {
            "dry-run-no-map" or "dry-run-no-map-template-exists" => ["repair", "project-ids"],
            "dry-run-with-map" or "dry-run-converged" => ["repair", "project-ids", "--map", mapPath],
            "queue-only-actionable" or "queue-only-attention" or "queue-only-converged" =>
                ["repair", "project-ids", "--apply", "--queue-only", "--map", mapPath],
            "apply-loop" => ["repair", "project-ids", "--apply", "--map", mapPath],
            "missing-map" => ["repair", "project-ids", "--map", Path.Combine(_dataRoot, "absent.json")],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        if (scenario == "dry-run-no-map-template-exists")
        {
            WriteFile(ProjectIdsRepairCommands.TemplateFileName, "{}");
        }

        var report = scenario switch
        {
            "dry-run-converged" or "queue-only-converged" => ConvergedReport(),
            "queue-only-attention" => AttentionReport(),
            _ => MixedReport()
        };
        var store = new InMemorySettings { ProjectIdsReport = report };
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await new ProjectIdsRepairCommands(store, ProjectIdsRepairCommands.RepairLoopOptions.Test)
            .RunAsync(parsed!.ParsedCliArgs, _dataRoot, new StandardStreams(TextReader.Null, stdout, stderr),
                TestContext.Current.CancellationToken);

        return $"exit {exit}; requested {store.LastRepairRequest?.ToString() ?? "nothing"}\n" +
               $"--- stdout\n{stdout}--- stderr\n{stderr}".Replace(_dataRoot, "<root>").Replace("\r\n", "\n");
    }

    private string WriteMap() => WriteFile("map.json", new ProjectIdAliasMap(
        [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa")],
        ["jsaa", "ai-badger"],
        ["qa-noise-project"]).ToJson(indented: true));

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dataRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Every bucket at once: fold, drop, retire, pin, unresolved with and without a registered name, orphan, canonical.</summary>
    private static ProjectIdCensusReport MixedReport() => new(
        [
            Row("jsaa", registered: true, entries: 3),
            Row("job-search-ai-assistant", entries: 2, nullContext: 1, queued: 4),
            Row("qa-noise-project", entries: 1),
            Row("empty-registered-project", registered: true),
            Row("mystery-guid-0001", entries: 1),
            Row("named-mystery", registeredName: "Named Mystery", entries: 1),
            Row("telemetry-only", metricsRows: 2),
            Row("ai-badger", registered: true, entries: 1)
        ], 0, 0, 0, 0, []);

    private static ProjectIdCensusReport AttentionReport() => new(
        [Row("jsaa", registered: true, entries: 3), Row("mystery-guid-0001", entries: 1)], 0, 0, 0, 0, []);

    private static ProjectIdCensusReport ConvergedReport() => new(
        [Row("jsaa", registered: true, entries: 3), Row("ai-badger", registered: true, entries: 1)], 0, 0, 0, 0, []);

    private static ProjectIdCensusRow Row(string projectId, bool registered = false, string? registeredName = null,
        long entries = 0, long nullContext = 0, long queued = 0, long metricsRows = 0) =>
        new(projectId, registered, registeredName, entries, 0, 0, 0, nullContext,
            0, 0, 0, 0, 0, 0, queued, 0, 0, 0, 0, 0, 0, 0, metricsRows, 0, []);

    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["dry-run-no-map"] = """
            exit 0; requested nothing
            --- stdout
            project-ids repair: 8 id(s) censused — 0 fold, 0 drop (test residue), 1 retire (registered, empty), 4 need a human to attribute, 1 pinned (waiting with reasons below), 2 need nothing (already correct or empty). Dry run — pass --apply to run this.
            project-ids repair: 4 id(s) own entries with no projects-table registration.
            project-ids repair: 1 id(s) pinned — waiting with reasons below:
              pinned-telemetry-only: 'telemetry-only' — owns only telemetry (2 metrics + 0 noise rows) — regenerable derived data the repair never moves
            project-ids repair: 'empty-registered-project' is registered with nothing left on it — retires (registry row removed)
            project-ids repair: 4 id(s) match no known id — left alone for a human to attribute:
              'job-search-ai-assistant'
              'qa-noise-project'
              'mystery-guid-0001'
              'named-mystery' (registered as 'Named Mystery')
            project-ids repair: no --map supplied — planned with the empty map (no folds). Wrote an editable alias-map template to '<root>/project-id-map.template.json' (example alias shape, __self_metrics__ + 2 registered canonical(s), 4 unattributed id(s) pre-filled in Dropped for review); edit Aliases/Dropped and re-run with --map.
            project-ids repair: re-running will not clear the 4 id(s) above that need a human — attribute them, or wait for an alias-map update.
            project-ids repair: summary — repair needed: 0 fold, 0 drop, 1 retire, 4 unresolved, 1 pinned (pinned-telemetry-only: 'telemetry-only') — pass --apply to run the loop until it reports converged; 4 id(s) still need a human to attribute.
            --- stderr

            """,
        ["dry-run-no-map-template-exists"] = """
            exit 0; requested nothing
            --- stdout
            project-ids repair: 8 id(s) censused — 0 fold, 0 drop (test residue), 1 retire (registered, empty), 4 need a human to attribute, 1 pinned (waiting with reasons below), 2 need nothing (already correct or empty). Dry run — pass --apply to run this.
            project-ids repair: 4 id(s) own entries with no projects-table registration.
            project-ids repair: 1 id(s) pinned — waiting with reasons below:
              pinned-telemetry-only: 'telemetry-only' — owns only telemetry (2 metrics + 0 noise rows) — regenerable derived data the repair never moves
            project-ids repair: 'empty-registered-project' is registered with nothing left on it — retires (registry row removed)
            project-ids repair: 4 id(s) match no known id — left alone for a human to attribute:
              'job-search-ai-assistant'
              'qa-noise-project'
              'mystery-guid-0001'
              'named-mystery' (registered as 'Named Mystery')
            project-ids repair: no --map supplied — planned with the empty map (no folds). Edit the existing template at '<root>/project-id-map.template.json' and re-run with --map.
            project-ids repair: re-running will not clear the 4 id(s) above that need a human — attribute them, or wait for an alias-map update.
            project-ids repair: summary — repair needed: 0 fold, 0 drop, 1 retire, 4 unresolved, 1 pinned (pinned-telemetry-only: 'telemetry-only') — pass --apply to run the loop until it reports converged; 4 id(s) still need a human to attribute.
            --- stderr

            """,
        ["dry-run-with-map"] = """
            exit 0; requested nothing
            --- stdout
            project-ids repair: 8 id(s) censused — 1 fold, 1 drop (test residue), 1 retire (registered, empty), 2 need a human to attribute, 1 pinned (waiting with reasons below), 2 need nothing (already correct or empty). Dry run — pass --apply to run this.
            project-ids repair: 4 id(s) own entries with no projects-table registration.
            project-ids repair: 'job-search-ai-assistant' owns 2 entries (1 NULL-context, fold), 4 queued — folds to 'jsaa'
            project-ids repair: 1 id(s) pinned — waiting with reasons below:
              pinned-telemetry-only: 'telemetry-only' — owns only telemetry (2 metrics + 0 noise rows) — regenerable derived data the repair never moves
            project-ids repair: 'empty-registered-project' is registered with nothing left on it — retires (registry row removed)
            project-ids repair: 'qa-noise-project' is test residue — deletes with a tombstone per removed hash
            project-ids repair: 2 id(s) match no known id — left alone for a human to attribute:
              'mystery-guid-0001'
              'named-mystery' (registered as 'Named Mystery')
            project-ids repair: re-running will not clear the 2 id(s) above that need a human — attribute them, or wait for an alias-map update.
            project-ids repair: summary — repair needed: 1 fold, 1 drop, 1 retire, 2 unresolved, 1 pinned (pinned-telemetry-only: 'telemetry-only') — pass --apply to run the loop until it reports converged; 2 id(s) still need a human to attribute.
            --- stderr

            """,
        ["dry-run-converged"] = """
            exit 0; requested nothing
            --- stdout
            project-ids repair: 2 id(s) censused — 0 fold, 0 drop (test residue), 0 retire (registered, empty), 0 need a human to attribute, 2 need nothing (already correct or empty). Dry run — pass --apply to run this.
            project-ids repair: summary — converged: 0 fold, 0 drop, 0 retire, 0 unresolved, 0 pinned, P3 inert (durable alias map empty — no id folds through, none is refused).
            --- stderr

            """,
        ["queue-only-actionable"] = """
            exit 0; requested ProjectIds
            --- stdout
            project-ids repair: 8 id(s) censused — 1 fold, 1 drop (test residue), 1 retire (registered, empty), 2 need a human to attribute, 1 pinned (waiting with reasons below), 2 need nothing (already correct or empty). Request will be queued for the server.
            project-ids repair: 4 id(s) own entries with no projects-table registration.
            project-ids repair: 'job-search-ai-assistant' owns 2 entries (1 NULL-context, fold), 4 queued — folds to 'jsaa'
            project-ids repair: 1 id(s) pinned — waiting with reasons below:
              pinned-telemetry-only: 'telemetry-only' — owns only telemetry (2 metrics + 0 noise rows) — regenerable derived data the repair never moves
            project-ids repair: 'empty-registered-project' is registered with nothing left on it — retires (registry row removed)
            project-ids repair: 'qa-noise-project' is test residue — deletes with a tombstone per removed hash
            project-ids repair: 2 id(s) match no known id — left alone for a human to attribute:
              'mystery-guid-0001'
              'named-mystery' (registered as 'Named Mystery')
            project-ids repair: re-running will not clear the 2 id(s) above that need a human — attribute them, or wait for an alias-map update.
            project-ids repair: request committed; the server applies it and drains the resulting embeddings on its next maintenance poll (~15s) — nothing left to run by hand.
            project-ids repair: the fold is single-pass — quiesce writers under a folded id, or re-run 'repair project-ids' until it reports no folds.
            project-ids repair: summary — repair in progress: 3 change(s) queued for the server — the server applies it on its next maintenance poll (~15s).
            --- stderr

            """,
        ["queue-only-attention"] = """
            exit 39; requested nothing
            --- stdout
            project-ids repair: 2 id(s) censused — 0 fold, 0 drop (test residue), 0 retire (registered, empty), 1 need a human to attribute, 1 need nothing (already correct or empty). Request will be queued for the server.
            project-ids repair: 1 id(s) own entries with no projects-table registration.
            project-ids repair: 1 id(s) match no known id — left alone for a human to attribute:
              'mystery-guid-0001'
            project-ids repair: re-running will not clear the 1 id(s) above that need a human — attribute them, or wait for an alias-map update.
            project-ids repair: summary — attention needed: 0 fold, 0 drop, 0 retire, 1 unresolved, 0 pinned — 1 id(s) still need a human to attribute.
            --- stderr

            """,
        ["queue-only-converged"] = """
            exit 0; requested nothing
            --- stdout
            project-ids repair: 2 id(s) censused — 0 fold, 0 drop (test residue), 0 retire (registered, empty), 0 need a human to attribute, 2 need nothing (already correct or empty). Request will be queued for the server.
            project-ids repair: summary — converged: 0 fold, 0 drop, 0 retire, 0 unresolved, 0 pinned, P3 inert (durable alias map empty — no id folds through, none is refused).
            --- stderr

            """,
        ["apply-loop"] = """
            exit 37; requested ProjectIds
            --- stdout
            project-ids repair: 8 id(s) censused — 1 fold, 1 drop (test residue), 1 retire (registered, empty), 2 need a human to attribute, 1 pinned (waiting with reasons below), 2 need nothing (already correct or empty). Request will be queued for the server.
            project-ids repair: 4 id(s) own entries with no projects-table registration.
            project-ids repair: 'job-search-ai-assistant' owns 2 entries (1 NULL-context, fold), 4 queued — folds to 'jsaa'
            project-ids repair: 1 id(s) pinned — waiting with reasons below:
              pinned-telemetry-only: 'telemetry-only' — owns only telemetry (2 metrics + 0 noise rows) — regenerable derived data the repair never moves
            project-ids repair: 'empty-registered-project' is registered with nothing left on it — retires (registry row removed)
            project-ids repair: 'qa-noise-project' is test residue — deletes with a tombstone per removed hash
            project-ids repair: 2 id(s) match no known id — left alone for a human to attribute:
              'mystery-guid-0001'
              'named-mystery' (registered as 'Named Mystery')
            project-ids repair: re-running will not clear the 2 id(s) above that need a human — attribute them, or wait for an alias-map update.
            project-ids repair: pass 1/3 — derived 1 fold, 1 drop, 1 retire; request committed; the server applies it on its next maintenance poll (~15s).
            project-ids repair: pass 1/3 — reaped: moved 0 row(s); census totals 9 → 9 entries.
            project-ids repair: stuck — identical actionable set across 2 passes with zero rows moved (actionable: D:qa-noise-project;F:job-search-ai-assistant->jsaa;R:empty-registered-project); quiesce writers under folded ids and check the server log for the job receipt, then re-run 'repair project-ids'.
            project-ids repair: summary — stuck: 1 fold, 1 drop, 1 retire, 2 unresolved, 1 pinned (pinned-telemetry-only: 'telemetry-only') — identical actionable set across 2 passes with zero rows moved; quiesce writers under folded ids, then re-run.
            --- stderr

            """,
        ["missing-map"] = """
            exit 15; requested nothing
            --- stdout
            --- stderr
            project-ids repair: cannot load --map '<root>/absent.json': Could not find file '<root>/absent.json'.

            """
    };
}
