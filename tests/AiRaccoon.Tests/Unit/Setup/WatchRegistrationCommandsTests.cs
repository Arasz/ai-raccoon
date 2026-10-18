using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     `watch add|remove|status` through the real dispatcher: the output mirrors the MCP tool result
///     fields one per line, and each server refusal exits non-zero with its fix.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchRegistrationCommandsTests
{
    private const string Project = "0b7c2b0e-6a8e-4f7e-9d1a-2f3c4d5e6f70";

    [Fact]
    public async Task Add_PrintsTheRegisteredPathAndEachPrunedWatch()
    {
        var control = new FakeWatchControl { Added = new WatchAddResponse(Project, "/repo", ["/repo/a", "/repo/b"], null) };

        var (exit, stdout, stderr) = await Run(["watch", "add", Project, "/repo"], control);

        exit.ShouldBe(0, stderr);
        Lines(stdout).ShouldBe([$"project: {Project}", "registered: /repo", "pruned: /repo/a", "pruned: /repo/b"]);
    }

    [Fact]
    public async Task Add_PrintsAbsorbedBy_ForAnExactReAdd()
    {
        var control = new FakeWatchControl { Added = new WatchAddResponse(Project, "/repo", [], "/repo") };

        var (exit, stdout, _) = await Run(["watch", "add", Project, "/repo"], control);

        exit.ShouldBe(0);
        Lines(stdout).ShouldBe([$"project: {Project}", "registered: /repo", "absorbedBy: /repo"]);
    }

    [Fact]
    public async Task Add_SendsTheAbsolutePath()
    {
        var control = new FakeWatchControl { Added = new WatchAddResponse(Project, "x", [], null) };

        await Run(["watch", "add", Project, "relative/dir"], control);

        control.LastPath.ShouldBe(Path.GetFullPath("relative/dir"));
        control.LastProjectId.ShouldBe(Project);
    }

    [Fact]
    public async Task Remove_PrintsTheRemovedPath()
    {
        var control = new FakeWatchControl();

        var (exit, stdout, _) = await Run(["watch", "remove", Project, "relative/dir"], control);

        exit.ShouldBe(0);
        control.LastPath.ShouldBe(Path.GetFullPath("relative/dir"));
        Lines(stdout).ShouldBe([$"removed: {Path.GetFullPath("relative/dir")}"]);
    }

    [Fact]
    public async Task Remove_OfAnUnwatchedPath_SaysNothingWasRemoved_AndExitsZero()
    {
        var control = new FakeWatchControl { RemoveFound = false };

        var (exit, stdout, _) = await Run(["watch", "remove", Project, "relative/dir"], control);

        exit.ShouldBe(0);
        Lines(stdout).ShouldBe([$"not watched: {Path.GetFullPath("relative/dir")} (nothing removed)"]);
    }

    [Fact]
    public async Task Remove_AgainstAnOlderServerThatSendsNoRemovedField_PrintsTheRemovedPath()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<WatchRemoveResponse>(
            $$"""{"projectId":"{{Project}}","path":"/x"}""", System.Text.Json.JsonSerializerOptions.Web)!;
        var control = new FakeWatchControl { RemoveFound = legacy.Removed };

        var (exit, stdout, _) = await Run(["watch", "remove", Project, "relative/dir"], control);

        exit.ShouldBe(0);
        Lines(stdout).ShouldBe([$"removed: {Path.GetFullPath("relative/dir")}"]);
    }

    [Fact]
    public async Task Status_PrintsOneBlockPerWatch_WithLastErrorOnlyWhenPresent()
    {
        var control = new FakeWatchControl
        {
            Statuses =
            [
                new WatchStatus(Project, "/repo", WatchState.Healthy, null, new DateTimeOffset(2026, 10, 17, 8, 30, 0, TimeSpan.Zero)),
                new WatchStatus(Project, "/docs", WatchState.Retrying, "disk gone")
            ]
        };

        var (exit, stdout, _) = await Run(["watch", "status", Project], control);

        exit.ShouldBe(0);
        control.LastProjectId.ShouldBe(Project);
        Lines(stdout).ShouldBe([
            $"project: {Project}", "path: /repo", "state: healthy", "lastSync: 2026-10-17T08:30:00Z", "",
            $"project: {Project}", "path: /docs", "state: retrying", "lastSync: never", "lastError: disk gone"
        ]);
    }

    [Fact]
    public async Task Status_ListsSkippedFilesWithReasons_AndCountsTheOnesBeyondTheList()
    {
        var control = new FakeWatchControl
        {
            Statuses =
            [
                new WatchStatus(Project, "/nb", WatchState.Healthy, null, null, 3,
                    [new SkippedFile("/nb/a.bin", SkipReason.Binary), new SkippedFile("/nb/b.xyz", SkipReason.UnsupportedType)])
            ]
        };

        var (exit, stdout, _) = await Run(["watch", "status", Project], control);

        exit.ShouldBe(0);
        Lines(stdout).ShouldBe([
            $"project: {Project}", "path: /nb", "state: healthy", "lastSync: never",
            "skipped: 3", "  /nb/a.bin (binary)", "  /nb/b.xyz (unsupported-type)", "  … 1 more"
        ]);
    }

    [Fact]
    public async Task Status_WithoutAProjectId_AsksForEveryProject()
    {
        var control = new FakeWatchControl { LastProjectId = "unset" };

        var (exit, stdout, _) = await Run(["watch", "status"], control);

        exit.ShouldBe(0);
        control.LastProjectId.ShouldBeNull();
        Lines(stdout).ShouldBe(["no registered watches"]);
    }

    [Theory]
    [InlineData("watching-disabled", ErrorCode.Usage.InvalidValue, $"ai-raccoon settings watch enable {Project} true")]
    [InlineData("path-outside-scope", ErrorCode.Usage.InvalidValue, $"ai-raccoon settings ingest scope add {Project} /repo")]
    [InlineData("path-not-found", ErrorCode.Usage.InvalidValue, "a path that exists")]
    [InlineData("watch-overlap", ErrorCode.Usage.InvalidValue, "/cover")]
    [InlineData("project-not-registered", ErrorCode.Usage.ProjectUnknown, $"ai-raccoon project id register {Project}")]
    [InlineData("project-retired", ErrorCode.Usage.ProjectUnknown, "ai-raccoon project id check")]
    public async Task Add_Refused_ExitsNonZero_WithTheFix(string kind, int expectedExit, string fix)
    {
        var refusal = new WatchRefusal(kind, $"{kind} reason", kind == "watch-overlap" ? "/cover" : null);
        var control = new FakeWatchControl { Refusal = WatchRefusedException.For(refusal, Project, "/repo") };

        var (exit, stdout, stderr) = await Run(["watch", "add", Project, "/repo"], control);

        exit.ShouldBe(expectedExit);
        stdout.ShouldBeEmpty();
        stderr.ShouldStartWith($"ai-raccoon: {kind}: ");
        stderr.ShouldContain(fix);
    }

    private static string[] Lines(string output) => output.TrimEnd().ReplaceLineEndings("\n").Split('\n');

    private static Task<(int Exit, string Out, string Err)> Run(string[] args, IWatchControl control) =>
        CliRun.RunAsync(args, TestData.CreateConfigCommands(new FakeConfigStore(),
            watchRegistrations: new WatchRegistrationCommands(control)));

    private sealed class FakeWatchControl : IWatchControl
    {
        public WatchAddResponse Added { get; init; } = new(Project, "/", [], null);

        public IReadOnlyList<WatchStatus> Statuses { get; init; } = [];

        public WatchRefusedException? Refusal { get; init; }

        public bool? RemoveFound { get; init; } = true;

        public string? LastProjectId { get; set; }

        public string? LastPath { get; private set; }

        public Task<WatchAddResponse> AddAsync(string projectId, string path, CancellationToken cancellationToken = default)
        {
            Record(projectId, path);
            return Task.FromResult(Added);
        }

        public Task<WatchRemoveResponse> RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default)
        {
            Record(projectId, path);
            return Task.FromResult(new WatchRemoveResponse(projectId, path, RemoveFound));
        }

        public Task<IReadOnlyList<WatchStatus>> StatusAsync(string? projectId, CancellationToken cancellationToken = default)
        {
            Record(projectId, null);
            return Task.FromResult(Statuses);
        }

        private void Record(string? projectId, string? path)
        {
            if (Refusal is not null)
            {
                throw Refusal;
            }

            LastProjectId = projectId;
            LastPath = path;
        }
    }
}
