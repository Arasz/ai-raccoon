using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     §3.3 D-E9 at the CLI seam: `model code set local &lt;dir&gt;` pre-flights the manifest
///     (missing/invalid surfaces the loader's own message) and delegates to the store, which
///     accepts ANY manifest dimension and reconciles vec_code to it (vec-code-unfix-dim — the
///     old 768 refusal gate is gone from both the CLI and the store). Witness test (the non-768
///     acceptance) is load-bearing.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ModelSetCodeLocalTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "ManifestFixtures", name);

    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
        {
            var commands = new SettingsCommands();
            return commands.ModelSetCodeLocalAsync(parsed.ParsedCliArgs, store, streams, ct);
        });

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-model-set-code-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SeedManifestDirectory(string dir, string fixtureName)
    {
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        File.Copy(FixturePath(fixtureName), Path.Combine(dir, EmbeddingManifest.FileName));
    }

    [Fact]
    public async Task ModelSetCodeLocal_Non768Manifest_ActivatesAndReconciles()
    {
        var dir = TempDir();
        SeedManifestDirectory(dir, "code-daemon-embed-v1-non768.json");
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["model", "set", "code", "local", dir], store);

        exit.ShouldBe(0, "any manifest dimension is accepted — the store reconciles vec_code to it");
        store.CodeActivated.ShouldNotBeNull();
        store.CodeActivated!.Value.Directory.ShouldBe(Path.GetFullPath(dir));
    }

    [Fact]
    public async Task ModelSetCodeLocal_DirectoryWithoutManifest_RefusesWithTheLoadersOwnMessage()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Run(["model", "set", "code", "local", dir], store));

        ex.Message.ShouldContain(EmbeddingManifest.FileName, customMessage: "the loader's own missing-manifest message must surface");
        store.CodeActivated.ShouldBeNull();
    }

    [Fact]
    public async Task ModelSetCodeLocal_Valid768Manifest_ActivatesTheCodeEngine()
    {
        var dir = TempDir();
        SeedManifestDirectory(dir, "code-daemon-embed-v1.json");
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["model", "set", "code", "local", dir], store);

        exit.ShouldBe(0);
        store.CodeActivated.ShouldNotBeNull();
        store.CodeActivated!.Value.Directory.ShouldBe(Path.GetFullPath(dir));
    }
}
