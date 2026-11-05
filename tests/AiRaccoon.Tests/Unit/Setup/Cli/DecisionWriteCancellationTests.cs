using System.Text.Json;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Ctrl-C during a decision write: once the request was sent the server may have committed it, so the
///     CLI says the outcome is unknown and how to replay it; before sending, nothing changed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DecisionWriteCancellationTests : IDisposable
{
    private const string Project = "0b7c2b0e-6a8e-4f7e-9d1a-2f3c4d5e6f70";

    private readonly string _root = TestData.CreateTempRoot("ai-raccoon-decision-cancel");

    public void Dispose() => TestData.DeleteTempRoot(_root);

    public static TheoryData<string[]> Writes() => new()
    {
        new[] { "decisions", "task", "register", "--project-id", Project, "--task-id", "t", "--title", "T" },
        new[] { "decisions", "record", "--project-id", Project, "--task-id", "t", "--input", "{input}" }
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task CtrlCAfterTheWriteWasSent_SaysTheOutcomeIsUnknownAndHowToReplay(string[] argv)
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeDecisionClient
        {
            Respond = async (_, ct) =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return JsonDocument.Parse(FakeDecisionClient.Envelope);
            }
        };
        var input = await InputAsync();

        var (exit, stdout, stderr) = await Run([.. argv.Select(a => a == "{input}" ? input : a), "--session-id", "s-9", "--request-id", "r-9"], client, cts.Token);

        exit.ShouldBe(ErrorCode.Ok.SIGC);
        stdout.ShouldBeEmpty();
        stderr.ShouldNotContain("changed nothing");
        stderr.ShouldContain("outcome is unknown");
        stderr.ShouldContain("--session-id s-9 --request-id r-9");
    }

    [Fact]
    public async Task CtrlCBeforeTheRecordWasSent_SaysNothingChanged()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = new FakeDecisionClient();
        var input = await InputAsync();

        var (exit, _, stderr) = await Run(["decisions", "record", "--project-id", Project, "--task-id", "t", "--input", input], client, cts.Token);

        exit.ShouldBe(ErrorCode.Ok.SIGC);
        client.Calls.ShouldBeEmpty();
        stderr.ShouldContain("changed nothing");
    }

    private async Task<string> InputAsync()
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"title":"T","category":"c","scenario":"s","outcome":"o","reasoning":"r"}""", TestContext.Current.CancellationToken);
        return path;
    }

    private static Task<(int Exit, string Out, string Err)> Run(string[] argv, FakeDecisionClient client, CancellationToken cancellationToken)
    {
        var commands = TestData.CreateConfigCommands(new FakeConfigStore(), decisions: new DecisionCommands(client));
        return CliRun.RunAsync(argv, (parsed, streams, _) => commands.RunAsync(parsed, streams, cancellationToken));
    }
}
