using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Memory;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Cli;

/// <summary>
///     `ai-raccoon decisions …` against a real server: the CLI's selection must equal the backend's,
///     its output must be complete or absent, and terminal text must be safe to print.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionCliTests : IAsyncLifetime
{
    private readonly string _project = Guid.CreateVersion7().ToString("D");
    private readonly string _outputDirectory = TestData.CreateTempRoot("ai-raccoon-decision-cli");
    private readonly CuttingHandler _cutter = new();
    private DecisionHost _host = null!;
    private HttpClient _cliHttp = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => $"/decisions/projects/{_project}";

    public async ValueTask InitializeAsync()
    {
        _host = await DecisionHost.StartAsync("decision-cli", Ct);
        await _host.RegisterAsync(_project, Ct);
        _cutter.InnerHandler = new HttpClientHandler();
        _cliHttp = new HttpClient(_cutter) { BaseAddress = _host.Http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
    }

    public async ValueTask DisposeAsync()
    {
        _cliHttp.Dispose();
        await _host.DisposeAsync();
        TestData.DeleteTempRoot(_outputDirectory);
    }

    public static TheoryData<string[], string, string[]> Selections() => new()
    {
        { [], "{}", ["t3"] },
        { ["--tasks", "2"], """{"selector":{"last":2}}""", ["t2", "t3"] },
        { ["--all"], """{"selector":{"all":true}}""", ["t1", "t2", "t3"] },
        { ["--task-id", "t1", "--task-id", "t2"], """{"selector":{"taskIds":["t1","t2"]}}""", ["t1", "t2"] }
    };

    [RetryTheory]
    [MemberData(nameof(Selections))]
    public async Task DecisionCli_DefaultNAllMatchBackend(string[] selection, string backendBody, string[] expectedTasks)
    {
        await SeedAsync();

        var (exit, stdout, stderr) = await Cli(["export", "--project-id", _project, .. selection]);

        exit.ShouldBe(0, stderr);
        var cli = JsonNode.Parse(stdout)!.AsObject();
        var backend = await BackendExportAsync(backendBody);
        cli["projectId"]!.GetValue<string>().ShouldBe(_project);
        cli["projectRevision"]!.GetValue<long>().ShouldBe(backend["projectRevision"]!.GetValue<long>());
        cli["selectedTaskIds"]!.AsArray().Select(id => id!.GetValue<string>()).ShouldBe(expectedTasks, ignoreOrder: true);
        JsonNode.DeepEquals(Snapshot(cli), Snapshot(backend["value"]!.AsObject())).ShouldBeTrue(
            $"the CLI export differs from the backend's\nCLI: {stdout}\nbackend: {backend["value"]}");
    }

    [RetryFact]
    public async Task DecisionCli_EmptyProjectExportsEmptyArrays()
    {
        var (exit, stdout, stderr) = await Cli(["export", "--project-id", _project]);

        exit.ShouldBe(0, stderr);
        var cli = JsonNode.Parse(stdout)!.AsObject();
        cli["schemaVersion"]!.GetValue<int>().ShouldBe(1);
        foreach (var name in new[] { "selectedTaskIds", "tasks", "decisions", "edges", "boundaryNodes", "referencedTasks", "importRecords" })
        {
            cli[name]!.AsArray().ShouldBeEmpty(name);
        }
    }

    [RetryFact]
    public async Task DecisionCli_LatestIsTheLastRegisteredTask_AndConcurrentExportsAgree()
    {
        await SeedAsync();
        var (registered, _, registerErr) = await Cli(["task", "register", "--project-id", _project, "--task-id", "t4", "--title", "Fourth"]);
        registered.ShouldBe(0, registerErr);

        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Cli(["export", "--project-id", _project, "--tasks", "2"])));

        foreach (var (exit, _, stderr) in runs)
        {
            exit.ShouldBe(0, stderr);
        }

        var snapshots = runs.Select(run => JsonNode.Parse(run.Out)!.AsObject()).ToArray();
        snapshots[0]["selectedTaskIds"]!.AsArray().Select(id => id!.GetValue<string>()).ShouldBe(["t3", "t4"], ignoreOrder: true);
        foreach (var snapshot in snapshots.Skip(1))
        {
            JsonNode.DeepEquals(Snapshot(snapshot), Snapshot(snapshots[0])).ShouldBeTrue("two exports at one revision differ");
        }
    }

    [RetryFact]
    public async Task DecisionCli_TaskRegister_PrintsGeneratedIdsToStderrAndTheTaskToStdout()
    {
        var (exit, stdout, stderr) = await Cli(["task", "register", "--project-id", _project, "--task-id", "t1", "--title", "First"]);

        exit.ShouldBe(0, stderr);
        stderr.ShouldContain("session-id");
        stderr.ShouldContain("request-id");
        var result = JsonNode.Parse(stdout)!.AsObject();
        result["value"]!["taskId"]!.GetValue<string>().ShouldBe("t1");
        result["replayed"]!.GetValue<bool>().ShouldBeFalse();
    }

    [RetryFact]
    public async Task DecisionCli_TaskRegister_ReplaysWithTheSameRequestId()
    {
        string[] argv = ["task", "register", "--project-id", _project, "--task-id", "t1", "--title", "First", "--session-id", "s", "--request-id", "r1"];
        (await Cli(argv)).Exit.ShouldBe(0);

        var (exit, stdout, stderr) = await Cli(argv);

        exit.ShouldBe(0, stderr);
        JsonNode.Parse(stdout)!["replayed"]!.GetValue<bool>().ShouldBeTrue();
        stderr.ShouldNotContain("request-id");
    }

    [RetryFact]
    public async Task DecisionCli_UnregisteredProject_ExitsProjectUnknown()
    {
        var (exit, stdout, stderr) = await Cli(["export", "--project-id", Guid.CreateVersion7().ToString("D")]);

        exit.ShouldBe(ErrorCode.Usage.ProjectUnknown);
        stdout.ShouldBeEmpty();
        stderr.ShouldStartWith("ai-raccoon: ");
    }

    [RetryFact]
    public async Task DecisionCli_InterruptedFileIsNotPublished()
    {
        await SeedAsync();
        var destination = Path.Combine(_outputDirectory, "export.json");
        await File.WriteAllTextAsync(destination, "previous export", Ct);
        _cutter.CutExportAfterBytes = 64;

        var (exit, stdout, stderr) = await Cli(["export", "--project-id", _project, "--all", "--output", destination, "--force"]);

        exit.ShouldNotBe(0);
        stdout.ShouldBeEmpty();
        stderr.ShouldContain("nothing was published");
        (await File.ReadAllTextAsync(destination, Ct)).ShouldBe("previous export");
        Directory.GetFiles(_outputDirectory).ShouldBe([destination]);
    }

    [RetryFact]
    public async Task DecisionCli_InterruptedStdoutExportPrintsNothingAndFails()
    {
        await SeedAsync();
        _cutter.CutExportAfterBytes = 64;

        var (exit, stdout, _) = await Cli(["export", "--project-id", _project, "--all"]);

        exit.ShouldNotBe(0);
        stdout.ShouldBeEmpty();
    }

    [RetryFact]
    public async Task DecisionCli_ExistingFileIsRefusedWithoutForce_AndReplacedOwnerOnlyWithIt()
    {
        await SeedAsync();
        var destination = Path.Combine(_outputDirectory, "export.json");
        await File.WriteAllTextAsync(destination, "previous export", Ct);

        var (refused, _, refusedErr) = await Cli(["export", "--project-id", _project, "--output", destination]);

        refused.ShouldBe(ErrorCode.Usage.InvalidValue);
        refusedErr.ShouldContain("--force");
        (await File.ReadAllTextAsync(destination, Ct)).ShouldBe("previous export");

        var (exit, stdout, stderr) = await Cli(["export", "--project-id", _project, "--output", destination, "--force"]);

        exit.ShouldBe(0, stderr);
        stdout.ShouldBeEmpty();
        JsonNode.Parse(await File.ReadAllTextAsync(destination, Ct))!["selectedTaskIds"]!.AsArray().Count.ShouldBe(1);
        Directory.GetFiles(_outputDirectory).ShouldBe([destination]);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(destination).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [RetryFact]
    public async Task DecisionCli_MarkdownExportKeepsFullTextAndEscapesTerminalAndMarkup()
    {
        await RegisterTaskAsync("t1", "First");
        var reasoning = string.Concat(Enumerable.Repeat("long rationale ", 40)) + "END";
        await RecordAsync("t1", "Hostile \u001b[31m<script>alert(1)</script> ‮title", reasoning);

        var (exit, stdout, stderr) = await Cli(["export", "--project-id", _project, "--format", "markdown"]);

        exit.ShouldBe(0, stderr);
        stdout.ShouldStartWith("# ");
        stdout.ShouldContain(reasoning);
        stdout.ShouldNotContain("\u001b");
        stdout.ShouldNotContain("‮");
        stdout.ShouldNotContain("<script>");
        stdout.ShouldContain("\\u001B");
    }

    [RetryFact]
    public async Task DecisionCli_PreviewTextSanitizesTerminalControls()
    {
        await RegisterTaskAsync("t1", "First");
        await RecordAsync("t1", "Bidi ‮evil⁦ title \u001b]0;pwned\u0007", "Why \u009b31m it matters");

        var (exit, stdout, stderr) = await Cli(["preview", "--project-id", _project]);

        exit.ShouldBe(0, stderr);
        stdout.ShouldContain("Bidi");
        foreach (var rune in stdout.EnumerateRunes())
        {
            (Rune.IsControl(rune) && rune.Value != '\n').ShouldBeFalse($"control U+{rune.Value:X4} reached the terminal");
            Rune.GetUnicodeCategory(rune).ShouldNotBe(System.Globalization.UnicodeCategory.Format, $"format U+{rune.Value:X4} reached the terminal");
        }
    }

    [RetryFact]
    public async Task DecisionCli_PreviewTextStaysWithinTheUnicodeBudget()
    {
        await RegisterTaskAsync("t1", "First");
        for (var i = 0; i < 12; i++)
        {
            await RecordAsync("t1", $"Decision {i} " + string.Concat(Enumerable.Repeat("\U0001F99D", 60)),
                string.Concat(Enumerable.Repeat("\U0001F4DA", 300)));
        }

        var (exit, stdout, stderr) = await Cli(["preview", "--project-id", _project, "--max-chars", "1000", "--max-decisions", "50"]);

        exit.ShouldBe(0, stderr);
        stdout.EnumerateRunes().Count().ShouldBeLessThanOrEqualTo(1000);
        stdout.EnumerateRunes().ShouldNotContain(Rune.ReplacementChar);
        stdout.ShouldContain("omitted");
    }

    [RetryFact]
    public async Task DecisionCli_PreviewJsonCarriesCountsAndBudgetedText()
    {
        await SeedAsync();

        var (exit, stdout, stderr) = await Cli(["preview", "--project-id", _project, "--all", "--format", "json", "--max-decisions", "1"]);

        exit.ShouldBe(0, stderr);
        var preview = JsonNode.Parse(stdout)!.AsObject();
        preview["projectId"]!.GetValue<string>().ShouldBe(_project);
        preview["total"]!["tasks"]!.GetValue<int>().ShouldBe(3);
        preview["shown"]!["decisions"]!.GetValue<int>().ShouldBe(1);
        preview["omitted"]!["decisions"]!.GetValue<int>().ShouldBe(2);
        preview["text"]!.GetValue<string>().ShouldContain("Shown tasks");
    }

    private async Task<(int Exit, string Out, string Err)> Cli(string[] args)
    {
        var store = new LazyServerSettingsStore(_ =>
            Task.FromResult<ISettingsStore>(new ServerSettingsStore(_cliHttp, DecisionHost.Token, CliSettingsBackend.RequestDeadline)));
        var commands = TestData.CreateConfigCommands(new FakeConfigStore(), decisions: new DecisionCommands(store));
        return await CliRun.RunAsync(["decisions", .. args], commands);
    }

    /// <summary>t1 holds two linked decisions, t2 none, t3 one that depends on a t1 decision.</summary>
    private async Task SeedAsync()
    {
        await RegisterTaskAsync("t1", "First");
        var a = await RecordAsync("t1", "Store decisions in the bank", "Reuse encryption");
        var b = await RecordAsync("t1", "Keep one bank", "One file to back up");
        await LinkAsync("t1", a, b);
        await RegisterTaskAsync("t2", "Second");
        await RegisterTaskAsync("t3", "Third");
        var c = await RecordAsync("t3", "Expose a CLI", "Exports outgrow MCP");
        await LinkAsync("t3", c, a);
    }

    private async Task RegisterTaskAsync(string taskId, string title) =>
        (await _host.Http.PostAsJsonAsync(Root + "/tasks", new { taskId, sessionId = "seed", requestId = "task-" + taskId, title }, Ct)).EnsureSuccessStatusCode();

    private async Task<string> RecordAsync(string taskId, string title, string reasoning)
    {
        var response = await _host.Http.PostAsJsonAsync(Root + "/records", new
        {
            taskId, sessionId = "seed", requestId = Guid.NewGuid().ToString("N"), title, category = "architecture",
            scenario = "Scenario", outcome = "Outcome", reasoning
        }, Ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!["value"]!["decisionId"]!.GetValue<string>();
    }

    private async Task LinkAsync(string taskId, string sourceId, string targetId)
    {
        var revision = (await BackendExportAsync("{}"))["projectRevision"]!.GetValue<long>();
        (await _host.Http.PostAsJsonAsync(Root + "/edges", new
        {
            taskId, sessionId = "seed", requestId = Guid.NewGuid().ToString("N"), sourceId, targetId, kind = "depends_on",
            reason = "because", expectedProjectRevision = revision
        }, Ct)).EnsureSuccessStatusCode();
    }

    private async Task<JsonObject> BackendExportAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _host.Http.PostAsync(Root + "/export", content, Ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["data"]!.AsObject();
    }

    /// <summary>The export's records without the per-call timestamp and the CLI's project header.</summary>
    private static JsonObject Snapshot(JsonObject export)
    {
        var copy = export.DeepClone().AsObject();
        copy.Remove("exportedAt");
        copy.Remove("projectId");
        copy.Remove("projectRevision");
        return copy;
    }

    /// <summary>Passes the real server's bytes through, failing an export body after a set number of them.</summary>
    private sealed class CuttingHandler : DelegatingHandler
    {
        public int? CutExportAfterBytes { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (CutExportAfterBytes is { } limit && request.RequestUri!.AbsolutePath.EndsWith("/export", StringComparison.Ordinal))
            {
                var original = response.Content;
                var cut = new StreamContent(new CutStream(await original.ReadAsStreamAsync(cancellationToken), limit));
                foreach (var header in original.Headers)
                {
                    cut.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                cut.Headers.ContentLength = null;
                response.Content = cut;
            }

            return response;
        }
    }

    private sealed class CutStream(Stream inner, int limit) : Stream
    {
        private int _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= limit)
            {
                throw new IOException("the connection was reset mid-export");
            }

            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, limit - _read)], cancellationToken);
            _read += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
