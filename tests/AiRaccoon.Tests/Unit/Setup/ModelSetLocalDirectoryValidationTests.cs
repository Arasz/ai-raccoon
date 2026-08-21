using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP3 M3/M4 at the CLI seam: `model set local &lt;dir&gt;` refuses a directory without a
///     manifest.json and a manifest whose dimensions are not 384 — BEFORE the migration outbox
///     commits (the same pre-commit-refusal principle as D10's probe). Only the legacy .onnx-file
///     path keeps today's defaults.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ModelSetLocalDirectoryValidationTests
{
    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
        {
            var commands = new SettingsCommands();
            return commands.ModelSetLocalAsync(parsed.ParsedCliArgs, store, store, streams, ct);
        });

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-model-set-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ModelSetLocal_DirectoryWithoutManifest_RefusesBeforeTheOutboxCommits()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Run(["model", "set", "local", dir], store));

        ex.Message.ShouldContain("manifest.json", customMessage: "the refusal must name the missing manifest");
        ex.Message.ShouldContain("model download", customMessage: "the refusal must point at the fix");
        store.Configured.ShouldBeNull("a refused model set must not commit the migration outbox");
    }

    [Fact]
    public async Task ModelSetLocal_DirectoryWithNon384Manifest_RefusesBeforeTheOutboxCommits()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "vocab.txt"), "vocab");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), """
            {
              "manifestVersion": 1,
              "model": "cli-refusal-test",
              "source": { "repo": "test/cli-manifest", "revision": "main" },
              "provider": "local",
              "dimensions": 1024,
              "contextWindowTokens": 8192,
              "normalization": "l2",
              "tokenizer": {
                "family": "bert-wordpiece",
                "files": [ { "path": "vocab.txt", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ]
              },
              "onnx": {
                "files": [ { "path": "model.onnx", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" } ],
                "inputs": [ "input_ids", "attention_mask", "token_type_ids" ],
                "tokenEmbeddingsOutput": "last_hidden_state"
              },
              "pooling": { "mode": "mean" }
            }
            """);
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => Run(["model", "set", "local", dir], store));

        ex.Message.ShouldContain("1024");
        ex.Message.ShouldContain("384");
        store.Configured.ShouldBeNull("a refused model set must not commit the migration outbox");
    }

    [Fact]
    public async Task ModelSetLocal_Valid384ManifestDirectory_StillCommits()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "vocab.txt"), "vocab");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), """
            {
              "manifestVersion": 1,
              "model": "cli-ok-test",
              "source": { "repo": "test/cli-manifest", "revision": "main" },
              "provider": "local",
              "dimensions": 384,
              "contextWindowTokens": 256,
              "normalization": "l2",
              "tokenizer": {
                "family": "bert-wordpiece",
                "files": [ { "path": "vocab.txt", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ]
              },
              "onnx": {
                "files": [ { "path": "model.onnx", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" } ],
                "inputs": [ "input_ids", "attention_mask", "token_type_ids" ],
                "tokenEmbeddingsOutput": "last_hidden_state"
              },
              "pooling": { "mode": "mean" }
            }
            """);
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["model", "set", "local", dir], store);

        exit.ShouldBe(0);
        store.Configured.ShouldNotBeNull();
        store.Configured!.Value.Model.ShouldBe(dir);
    }

    [Fact]
    public async Task ModelSetLocal_LegacyOnnxFile_IsUnchanged()
    {
        var file = Path.Combine(Path.GetTempPath(), "ai-raccoon-model-set-tests", Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllText(file, "model");
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["model", "set", "local", file], store);

        exit.ShouldBe(0);
        store.Configured.ShouldNotBeNull();
        store.Configured!.Value.Model.ShouldBe(file);
    }
}
