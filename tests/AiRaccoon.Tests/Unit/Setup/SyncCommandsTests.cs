using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Direct tests of the extracted SyncCommands component (sync add/remove/show verb family).
///     The full behavior contract stays in ConfigCommandsRetrievalSweepSyncTests through the
///     dispatcher; these pin the component seam + the interactive-secret UX.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SyncCommandsTests
{
    /// <summary>Calls the component method directly — no dispatcher, that is the seam under test.</summary>
    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store,
        TextReader? stdin = null) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
        {
            var commands = new SyncCommands();
            return parsed.CommandPath switch
            {
                ["sync", "add", "s3"] => commands.AddS3Async(parsed.ParsedCliArgs, store, streams, ct),
                ["sync", "add", "azure"] => commands.AddAzureAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["sync", "remove"] => commands.RemoveAsync(store, streams, ct),
                ["sync", "show"] => commands.ShowAsync(store, streams, ct),
                _ => throw new InvalidOperationException($"unhandled: {string.Join(' ', parsed.CommandPath)}")
            };
        }, stdin);

    [Fact]
    public async Task AddS3_PromptsOnStderr_WritesSecrets()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, stderr) = await Run(["sync", "add", "s3", "https://s3.example.com", "--bucket", "b"],
            store, new StringReader("ak1\nsk1\n"));

        exit.ShouldBe(0);
        stderr.ShouldContain("S3 access key");
        stderr.ShouldContain("S3 secret key");
        store.Settings["sync.provider"].ShouldBe("s3");
        store.Settings["sync.accessKey"].ShouldBe("ak1");
        store.Settings["sync.secretKey"].ShouldBe("sk1");
        stdout.ShouldContain("sync configured");
    }

    [Fact]
    public async Task AddS3_EmptyAccessKey_AbortsWithoutPersisting()
    {
        var store = new FakeConfigStore();

        var (exit, _, stderr) = await Run(["sync", "add", "s3", "https://s3.example.com", "--bucket", "b"],
            store, new StringReader("\n"));

        exit.ShouldBe(1);
        stderr.ShouldContain("access key required");
        store.Settings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_DeletesAllSyncRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "s3",
                ["sync.endpoint"] = "https://s3.example.com",
                ["sync.accessKey"] = "ak"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "remove"], store);

        exit.ShouldBe(0);
        store.Settings.ShouldBeEmpty();
        stdout.ShouldContain("sync removed");
    }

    [Fact]
    public async Task Show_NotConfigured_PrintsMessage()
    {
        var (exit, stdout, _) = await Run(["sync", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("sync not configured");
    }

    [Fact]
    public async Task Show_Azure_PrintsContainerAndRedactedConnectionString()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "azure",
                ["sync.container"] = "mem",
                ["sync.connectionString"] = "secret-conn"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("container: mem");
        stdout.ShouldContain("connectionString: set");
        stdout.ShouldNotContain("secret-conn");
    }
}
