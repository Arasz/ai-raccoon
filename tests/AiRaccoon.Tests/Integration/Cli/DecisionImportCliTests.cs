using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Memory;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Decisions;
using AiRaccoon.Tests.Unit.Setup;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Cli;

/// <summary>`decisions import-semantica` through real argv: preview prints the apply command, apply is bound to it, and bad flags or files send nothing.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionImportCliTests : IAsyncLifetime
{
    private readonly string _project = Guid.CreateVersion7().ToString("D");
    private readonly string _files = TestData.CreateTempRoot("ai-raccoon-import-cli");
    private DecisionHost _host = null!;
    private HttpClient _cliHttp = null!;
    private string _source = null!;
    private string _mapping = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await DecisionHost.StartAsync("decision-import-cli", Ct);
        await _host.RegisterAsync(_project, Ct);
        _cliHttp = new HttpClient { BaseAddress = _host.Http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        _source = Path.Combine(_files, "export.json");
        _mapping = Path.Combine(_files, "mapping.json");
        await File.WriteAllBytesAsync(_source, SemanticaParsingTests.Fixture("export-graph-json-0.7.0.json"), Ct);
        await File.WriteAllBytesAsync(_mapping, SemanticaParsingTests.Fixture("export-graph-json-0.7.0.mapping.json"), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _cliHttp.Dispose();
        await _host.DisposeAsync();
        TestData.DeleteTempRoot(_files);
    }

    private async Task<(int Exit, string Out, string Err)> RealCli(string[] args)
    {
        var store = new LazyServerSettingsStore(_ =>
            Task.FromResult<ISettingsStore>(new ServerSettingsStore(_cliHttp, DecisionHost.Token, CliSettingsBackend.RequestDeadline)));
        return await CliRun.RunAsync(["decisions", .. args], TestData.CreateConfigCommands(new FakeConfigStore(), decisions: new DecisionCommands(store)));
    }

    private static async Task<(int Exit, string Out, string Err)> FakeCli(FakeDecisionClient fake, string[] args) =>
        await CliRun.RunAsync(["decisions", .. args], TestData.CreateConfigCommands(new FakeConfigStore(), decisions: new DecisionCommands(fake)));

    private string[] Preview(params string[] extra) =>
        ["import-semantica", "--project-id", _project, "--input", _source, "--mapping", _mapping, .. extra];

    private async Task RegisterTasksAsync()
    {
        foreach (var (id, title) in new[] { ("semantica-legacy", "Legacy"), ("storage-task", "Storage") })
        {
            (await _host.Http.PostAsJsonAsync($"/decisions/projects/{_project}/tasks",
                new { taskId = id, sessionId = "seed", requestId = $"task-{id}", title }, Ct)).EnsureSuccessStatusCode();
        }
    }

    /// <summary>The words after `ai-raccoon decisions` in the apply command the preview printed on stderr.</summary>
    private static string[] PrintedApplyArgs(string stderr)
    {
        var line = stderr.Split('\n').Single(text => text.Contains("--apply", StringComparison.Ordinal)).Trim();
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        words[..2].ShouldBe(["ai-raccoon", "decisions"]);
        return words[2..];
    }

    [RetryFact]
    public async Task ImportSemantica_PreviewThenApply_AgainstARealServer()
    {
        await RegisterTasksAsync();

        var (previewExit, previewOut, previewErr) = await RealCli(Preview());

        previewExit.ShouldBe(0, previewErr);
        var report = JsonNode.Parse(previewOut)!.AsObject();
        report["canApply"]!.GetValue<bool>().ShouldBeTrue();
        report["projectRevision"]!.GetValue<long>().ShouldBe(2);
        report["counts"]!["decisions"]!.GetValue<int>().ShouldBe(4);
        var args = PrintedApplyArgs(previewErr);
        args.ShouldContain("--apply");
        args[Array.IndexOf(args, "--source-digest") + 1].ShouldBe(report["sourceDigest"]!.GetValue<string>());
        args[Array.IndexOf(args, "--mapping-digest") + 1].ShouldBe(report["mappingDigest"]!.GetValue<string>());
        args[Array.IndexOf(args, "--expected-project-revision") + 1].ShouldBe("2");
        args[Array.IndexOf(args, "--request-id") + 1].ShouldNotBeNullOrWhiteSpace();

        var (applyExit, applyOut, applyErr) = await RealCli(args);

        applyExit.ShouldBe(0, applyErr);
        var outcome = JsonNode.Parse(applyOut)!.AsObject();
        outcome["projectRevision"]!.GetValue<long>().ShouldBe(3);
        outcome["value"]!["counts"]!["decisions"]!.GetValue<int>().ShouldBe(4);
        outcome["replayed"]!.GetValue<bool>().ShouldBeFalse();
    }

    [RetryFact]
    public async Task ImportSemantica_PreviewWithBlockers_PrintsTheReportAndRefuses()
    {
        var (exit, stdout, stderr) = await RealCli(Preview());

        exit.ShouldBe(ErrorCode.Usage.RequestRejected, stderr);
        var report = JsonNode.Parse(stdout)!.AsObject();
        report["canApply"]!.GetValue<bool>().ShouldBeFalse();
        report["blockers"]!.AsArray().Select(blocker => blocker!["code"]!.GetValue<string>()).ShouldContain("unknown-task");
        stderr.ShouldContain("blocker");
        stderr.ShouldNotContain("--apply");
    }

    [RetryFact]
    public async Task ImportSemantica_ApplyWithoutCompanions_SendsNothing()
    {
        var fake = new FakeDecisionClient();
        string[] all = ["--apply", "--request-id", "r", "--source-digest", "A", "--mapping-digest", "B", "--expected-project-revision", "2"];

        for (var skip = 0; skip < 4; skip++)
        {
            var companions = new List<string> { "--apply" };
            var pairs = new[] { ("--request-id", "r"), ("--source-digest", "A"), ("--mapping-digest", "B"), ("--expected-project-revision", "2") };
            for (var index = 0; index < pairs.Length; index++)
            {
                if (index != skip)
                {
                    companions.AddRange([pairs[index].Item1, pairs[index].Item2]);
                }
            }

            var (exit, _, stderr) = await FakeCli(fake, Preview(companions.ToArray()));
            exit.ShouldBe(ErrorCode.Usage.InvalidValue, stderr);
        }

        string[][] lone = [["--request-id", "r"], ["--session-id", "s"], ["--source-digest", "A"], ["--mapping-digest", "B"], ["--expected-project-revision", "2"]];
        foreach (var flag in lone)
        {
            var (exit, _, stderr) = await FakeCli(fake, Preview(flag));
            exit.ShouldBe(ErrorCode.Usage.InvalidValue, stderr);
        }

        fake.Calls.ShouldBeEmpty();
        (await FakeCli(fake, Preview(all))).Exit.ShouldBe(0);
        fake.Calls.ShouldBe(["import-apply"]);
    }

    [RetryFact]
    public async Task ImportSemantica_RetryWithSameRequestId_Replays()
    {
        await RegisterTasksAsync();
        var (_, report, previewErr) = await RealCli(Preview());
        var args = PrintedApplyArgs(previewErr);
        report.ShouldNotBeNullOrEmpty();
        (await RealCli(args)).Exit.ShouldBe(0);

        var (exit, stdout, stderr) = await RealCli(args);

        exit.ShouldBe(0, stderr);
        var replay = JsonNode.Parse(stdout)!.AsObject();
        replay["replayed"]!.GetValue<bool>().ShouldBeTrue();
        replay["projectRevision"]!.GetValue<long>().ShouldBe(3);
    }

    [RetryFact]
    public async Task ImportSemantica_OversizedFile_SendsNothing()
    {
        var fake = new FakeDecisionClient();
        var big = Path.Combine(_files, "big.json");
        await using (var stream = new FileStream(big, FileMode.Create))
        {
            stream.SetLength(DecisionImportLimits.MaxBytes + 1L);
        }

        var (sourceExit, _, sourceErr) = await FakeCli(fake, ["import-semantica", "--project-id", _project, "--input", big, "--mapping", _mapping]);
        var (mappingExit, _, _) = await FakeCli(fake, ["import-semantica", "--project-id", _project, "--input", _source, "--mapping", big]);
        var (missingExit, _, _) = await FakeCli(fake, ["import-semantica", "--project-id", _project, "--input", Path.Combine(_files, "none.json"), "--mapping", _mapping]);

        sourceExit.ShouldBe(ErrorCode.Usage.InvalidValue, sourceErr);
        mappingExit.ShouldBe(ErrorCode.Usage.InvalidValue);
        missingExit.ShouldBe(ErrorCode.Environment.IoFailed);
        fake.Calls.ShouldBeEmpty();
    }
}
