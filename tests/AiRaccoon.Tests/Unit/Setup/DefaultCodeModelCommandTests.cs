using System.ComponentModel;
using System.Reflection;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     #422: a fresh install has no code engine, and every surface that can notice that must name
///     the SAME command to fix it. Six places used to be six chances to write a slightly different
///     string; they all read <see cref="CodeEngineSetup.DefaultModelCommand" /> now, and this pins
///     that they do — a hint nobody can copy correctly is worse than no hint.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DefaultCodeModelCommandTests
{
    [Fact]
    public void TheCommandString_ParsesAsARealVerb()
    {
        var parse = CliCommandTree.BuildFullRootCommand()
            .Parse(CodeEngineSetup.DefaultModelCommand.Split(' ')[1..]);

        parse.Errors.ShouldBeEmpty(
            $"'{CodeEngineSetup.DefaultModelCommand}' is quoted to users everywhere; it has to parse");
        parse.CommandResult.Command.Name.ShouldBe("default");
    }

    /// <summary>
    ///     `default` sits beside `local` under `model set code`, so the activating family stays the
    ///     activating family and `model download`'s "never activates" contract is untouched.
    /// </summary>
    [Fact]
    public void TheCommandString_IsUnderModelSetCode_NotUnderModelDownload()
    {
        CodeEngineSetup.DefaultModelCommand.ShouldBe("ai-raccoon model set code default");

        var parse = CliCommandTree.BuildFullRootCommand().Parse(["model", "download", "code"]);

        parse.CommandResult.Command.Name.ShouldBe("download",
            customMessage: "'model download' takes a repo id and never activates — 'code' must stay an "
                           + "argument value there, never become a verb that downloads-and-activates");
        parse.GetValue<string>("repo-id").ShouldBe("code");
    }

    /// <summary>
    ///     The one-shot's second run: an already-downloaded model is re-activated without re-fetching
    ///     187 MB, and the output never tells the reader to go run another command — the whole reason
    ///     `default` activates is that a two-step hint is one people do not finish.
    /// </summary>
    [Fact]
    public async Task ModelSetCodeDefault_WhenTheModelIsAlreadyDownloaded_ActivatesWithoutASecondCommand()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ai-raccoon-code-default", Guid.NewGuid().ToString("N"));
        var modelDir = Path.Combine(dataRoot, "models", "faxenoff__code-daemon-embed-v1");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(modelDir, "model.onnx"), "model");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Resources", "ManifestFixtures", "code-daemon-embed-v1.json"),
            Path.Combine(modelDir, EmbeddingManifest.FileName));
        var store = new FakeConfigStore();

        var (exit, outp, _) = await CliRun.RunAsync(
            ["model", "set", "code", "default"],
            (parsed, streams, ct) => new SettingsCommands().ModelSetCodeDefaultAsync(
                new ModelDownloadCommands(new UnusedHttpClientFactory()), store, dataRoot, streams, ct));

        exit.ShouldBe(0);
        store.CodeActivated!.Value.Directory.ShouldBe(modelDir);
        outp.ShouldContain("already downloaded");
        outp.ShouldNotContain("model set local",
            customMessage: "that hint points at the MEMORY engine and there is no second step to take here");
    }

    [Fact]
    public void TheSearchWarning_NamesTheCommand()
    {
        CodeSearchWarnings.EngineNotConfigured.ShouldContain(CodeEngineSetup.DefaultModelCommand);
        CodeSearchWarnings.EngineNotConfigured.ShouldStartWith(CodeSearchWarnings.EngineNotConfiguredPrefix);
    }

    [Fact]
    public void TheMcpServerInstructions_ExplainTheWarningAndNameTheCommand()
    {
        McpServerInstructions.Text.ShouldContain(CodeSearchWarnings.EngineNotConfiguredPrefix,
            customMessage: "an agent that cannot recognise the warning cannot relay it");
        McpServerInstructions.Text.ShouldContain(CodeEngineSetup.DefaultModelCommand);
    }

    [Fact]
    public void TheMemorySearchToolDescription_NamesTheCommand()
    {
        var description = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.Search), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        description.ShouldContain(CodeEngineSetup.DefaultModelCommand,
            customMessage: "the tool description is the only guidance a client that ignores server instructions sees");
    }

    [Fact]
    public void TheHowTo_QuotesTheCommandVerbatim()
    {
        var howTo = File.ReadAllText(TestData.RepoFile("docs/how-to/configure-embedding-engines.md"));

        howTo.ShouldContain(CodeEngineSetup.DefaultModelCommand);
        howTo.ShouldNotContain("contextWindowTokens` down to `128`",
            customMessage: "#422's manual workaround is gone; documenting it would send people back to the hand-edit");
    }

    /// <summary>The already-downloaded path never reaches the network; a factory that hands one out
    /// would hide a regression that started fetching.</summary>
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("an already-downloaded model must not be re-fetched");
    }
}
