using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Config command behaviors (access + model families) against an in-memory settings
///     store: the CLI is the single runtime config channel, so every command is a
///     settings-table write/read with a printed result and a process exit code.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsAccessModelTests
{
    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, TestData.CreateConfigCommands(store, settings: new SettingsCommands()));


    [Fact]
    public async Task AccessDefaultSet_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await Run(["access", "default", "set", "rw"], store);

        exit.ShouldBe(0);
        store.Settings["access.mode.global"].ShouldBe("rw");
        stdout.ShouldContain("rw");
    }

    [Fact]
    public async Task AccessDefaultSet_Full_RoundTrips()
    {
        var store = new FakeConfigStore();

        await Run(["access", "default", "set", "full"], store);

        store.Settings["access.mode.global"].ShouldBe("full");
    }

    [Fact]
    public async Task AccessDefaultSet_InvalidMode_ReturnsErrorAndWritesNothing()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["access", "default", "set", "bogus"], store);

        exit.ShouldBe(1);
        err.ShouldContain("bogus");
        store.Settings.ShouldNotContainKey("access.mode.global");
    }

    [Fact]
    public async Task AccessDefaultShow_NoRow_PrintsEffectiveDefaultRw()
    {
        var (exit, stdout, _) = await Run(["access", "default", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("rw");
    }

    [Fact]
    public async Task AccessDefaultShow_WithRow_PrintsRowValue()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["access.mode.global"] = "ro"
            }
        };

        var (_, stdout, _) = await Run(["access", "default", "show"], store);

        stdout.Trim().ShouldBe("ro");
    }


    [Fact]
    public async Task AccessSet_WritesPerProjectRow()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await Run(["access", "set", "acme", "full"], store);

        exit.ShouldBe(0);
        store.Settings["access.mode.project:acme"].ShouldBe("full");
        stdout.ShouldContain("acme");
    }

    [Fact]
    public async Task AccessSetStar_IsTheGlobalSpelling()
    {
        var store = new FakeConfigStore();

        await Run(["access", "set", "*", "ro"], store);

        // The global row IS the wildcard for access (findings: `access set *` is spelled
        // `access default set`); a literal wildcard project row must not be written.
        store.Settings["access.mode.global"].ShouldBe("ro");
        store.Settings.Keys.ShouldNotContain("access.mode.project:*");
    }

    [Fact]
    public async Task AccessUnset_RemovesPerProjectRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["access.mode.project:acme"] = "full"
            }
        };

        var (exit, _, _) = await Run(["access", "unset", "acme"], store);

        exit.ShouldBe(0);
        store.Settings.ShouldNotContainKey("access.mode.project:acme");
    }

    [Fact]
    public async Task AccessUnsetStar_RemovesGlobalRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["access.mode.global"] = "full"
            }
        };

        await Run(["access", "unset", "*"], store);

        store.Settings.ShouldNotContainKey("access.mode.global");
    }

    [Fact]
    public async Task AccessList_PrintsDefaultAndOverrides()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["access.mode.global"] = "rw",
                ["access.mode.project:acme"] = "full",
                ["access.mode.project:zeta"] = "ro"
            }
        };

        var (exit, stdout, _) = await Run(["access", "list"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("default: rw");
        stdout.ShouldContain("acme: full");
        stdout.ShouldContain("zeta: ro");
    }

    [Fact]
    public async Task AccessList_NoRows_PrintsOnlyDefaultRw()
    {
        var (_, stdout, _) = await Run(["access", "list"], new FakeConfigStore());

        stdout.Trim().ShouldBe("default: rw");
    }


    [Fact]
    public async Task ModelSetLocal_WithoutPath_UsesBundledModel()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await Run(["model", "set", "local"], store);

        exit.ShouldBe(0);
        store.Settings["embedding.provider"].ShouldBe("local");
        store.Settings.ShouldNotContainKey("embedding.model");
        store.Settings["embedding.engine"].ShouldBe("local:bundled");
        stdout.ShouldContain("local");
    }

    [Fact]
    public async Task ModelSetLocal_WithPath_PersistsAbsoluteModelPath()
    {
        var store = new FakeConfigStore();

        await Run(["model", "set", "local", "/models/custom.onnx"], store);

        store.Settings["embedding.model"].ShouldBe("/models/custom.onnx");
        store.Settings["embedding.engine"].ShouldBe("local:/models/custom.onnx");
    }

    [Fact]
    public async Task ModelSetLocal_SwitchingFromOpenAi_ClearsStaleBaseUrlAndApiKeyRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["embedding.provider"] = "openai",
                ["embedding.model"] = "text-embedding-3-small",
                ["embedding.baseUrl"] = "https://api.openai.com/v1",
                ["embedding.apiKey"] = "secret",
                ["embedding.engine"] = "openai:text-embedding-3-small@https://api.openai.com/v1"
            }
        };

        await Run(["model", "set", "local"], store);

        store.Settings["embedding.provider"].ShouldBe("local");
        store.Settings.ShouldNotContainKey("embedding.model");
        store.Settings.ShouldNotContainKey("embedding.baseUrl");
        store.Settings.ShouldNotContainKey("embedding.apiKey");
    }


    [Fact]
    public async Task ModelSetOpenAi_WithApiKey_PersistsProviderModelBaseUrlAndKey()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(
            ["model", "set", "openai", "text-embedding-3-small", "http://localhost:11434/v1", "--api-key", "k123"],
            store);

        exit.ShouldBe(0);
        store.Settings["embedding.provider"].ShouldBe("openai");
        store.Settings["embedding.model"].ShouldBe("text-embedding-3-small");
        store.Settings["embedding.baseUrl"].ShouldBe("http://localhost:11434/v1");
        store.Settings["embedding.apiKey"].ShouldBe("k123");
        store.Settings["embedding.engine"].ShouldBe("openai:text-embedding-3-small@http://localhost:11434/v1");
    }

    [Fact]
    public async Task ModelSetOpenAi_WithoutBaseUrl_ClearsBaseUrlRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["embedding.baseUrl"] = "https://stdin, TestContext.Current.CancellationToken.example.com",
                ["embedding.apiKey"] = "k"
            }
        };

        await Run(["model", "set", "openai", "m", "--api-key", "k"], store);

        store.Settings.ShouldNotContainKey("embedding.baseUrl");
        store.Settings["embedding.engine"].ShouldBe($"openai:m@{EmbeddingService.DefaultOpenAiEndpoint}");
    }

    [Fact]
    public async Task ModelSetOpenAi_WithoutApiKey_WarnsOnStderr()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["model", "set", "openai", "m"], store);

        exit.ShouldBe(0);
        err.ShouldContain("api key");
    }


    [Fact]
    public async Task ModelReset_DeletesAllEmbeddingRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["embedding.provider"] = "local",
                ["embedding.engine"] = "local:bundled",
                ["embedding.apiKey"] = "secret"
            }
        };

        var (exit, stdout, _) = await Run(["model", "reset"], store);

        exit.ShouldBe(0);
        store.Settings.Keys.ShouldNotContain(k => k.StartsWith("embedding.", StringComparison.Ordinal));
        stdout.ShouldContain("FTS5");
    }

    [Fact]
    public async Task ModelShow_NoEngine_PrintsNone()
    {
        var (exit, stdout, _) = await Run(["model", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: (none");
    }

    [Fact]
    public async Task ModelShow_WithEngine_PrintsProviderAndRedactedApiKey()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["embedding.provider"] = "openai",
                ["embedding.model"] = "text-embedding-3-small",
                ["embedding.engine"] = "openai:text-embedding-3-small@https://api.openai.com/v1",
                ["embedding.apiKey"] = "sk-secret"
            }
        };

        var (_, stdout, _) = await Run(["model", "show"], store);

        stdout.ShouldContain("openai");
        stdout.ShouldContain("text-embedding-3-small");
        stdout.ShouldContain("Key: set");
        stdout.ShouldNotContain("sk-secret");
    }

    [Fact]
    public async Task ModelSetLocal_TildePath_ExpandsToHome()
    {
        var store = new FakeConfigStore();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        await Run(["model", "set", "local", "~/models/custom.onnx"], store);

        store.Settings["embedding.model"].ShouldBe(Path.Combine(home, "models/custom.onnx"));
    }

    [Fact]
    public async Task UnhandledCommandPath_FallsThroughToError()
    {
        var store = new FakeConfigStore();
        CliArgs.TryParse(["access"], out var parsed);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await TestData.CreateConfigCommands(store)
            .RunAsync(parsed!, new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(1);
        stderr.ToString().ShouldContain("unhandled command");
    }
}
