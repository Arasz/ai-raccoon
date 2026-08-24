using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     §3.3 D-E9 at the CLI seam: `model set code local &lt;dir&gt;` refuses a manifest whose
///     dimensions are not 768 — THE only gate protecting vec_code float[768], since code has no
///     dimension-reconcile phase to fix up a wrong-dimension activation after the fact (unlike
///     the memory bank's `model set local`). Also refuses a missing/invalid manifest, surfacing
///     the loader's own message. Witness test (the non-768 refusal) is load-bearing.
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
    public async Task ModelSetCodeLocal_Non768Manifest_RefusesBeforeActivation_WithActionableMessage()
    {
        var dir = TempDir();
        SeedManifestDirectory(dir, "code-daemon-embed-v1-non768.json");
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Run(["model", "code", "set", "local", dir], store));

        ex.Message.ShouldContain("1024", customMessage: "the refusal must name the declared dimensions");
        ex.Message.ShouldContain(CodeCorpusSchema.EmbeddingDimensions.ToString(),
            customMessage: "the refusal must name the required dimensions");
        store.CodeActivated.ShouldBeNull("a refused code model set must not commit the activation transaction");
    }

    [Fact]
    public async Task ModelSetCodeLocal_DirectoryWithoutManifest_RefusesWithTheLoadersOwnMessage()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Run(["model", "code", "set", "local", dir], store));

        ex.Message.ShouldContain(EmbeddingManifest.FileName, customMessage: "the loader's own missing-manifest message must surface");
        store.CodeActivated.ShouldBeNull();
    }

    [Fact]
    public async Task ModelSetCodeLocal_Valid768Manifest_ActivatesTheCodeEngine()
    {
        var dir = TempDir();
        SeedManifestDirectory(dir, "code-daemon-embed-v1.json");
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["model", "code", "set", "local", dir], store);

        exit.ShouldBe(0);
        store.CodeActivated.ShouldNotBeNull();
        store.CodeActivated!.Value.Directory.ShouldBe(Path.GetFullPath(dir));
    }
}
