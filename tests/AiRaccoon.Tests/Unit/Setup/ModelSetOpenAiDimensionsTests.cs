using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP4 / plan D2: a remote engine declares its output dimension with `--dims`, persisted as
///     `embedding.dimensions` — sqlite-vec infers nothing, so the drain needs it to reconcile vec0
///     before writing. Key hygiene: `model set local` and `model reset` clear the row, so a 384
///     local engine never inherits a remote model's dimension.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ModelSetOpenAiDimensionsTests
{
    private static Task<(int Exit, string Out, string Err)> RunOpenAi(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
            new SettingsCommands().ModelSetOpenAiAsync(parsed.ParsedCliArgs, store, store, streams, ct));

    private static Task<(int Exit, string Out, string Err)> RunLocal(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
            new SettingsCommands().ModelSetLocalAsync(parsed.ParsedCliArgs, store, store, streams, ct));

    [Fact]
    public async Task ModelSetOpenAi_WithDims_PersistsTheDeclaredDimension()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await RunOpenAi(
            ["model", "embedding", "set", "openai", "text-embedding-3-large", "--api-key", "k", "--dims", "3072"], store);

        exit.ShouldBe(0);
        store.Settings[EmbeddingSettingsKeys.Dimensions].ShouldBe("3072");
    }

    [Fact]
    public async Task ModelSetOpenAi_WithoutDims_LeavesTheRowUnset()
    {
        var store = new FakeConfigStore();

        await RunOpenAi(["model", "embedding", "set", "openai", "text-embedding-3-small", "--api-key", "k"], store);

        store.Settings.ShouldNotContainKey(EmbeddingSettingsKeys.Dimensions,
            "an undeclared dimension keeps the legacy 384 assumption rather than inventing one");
    }

    [Fact]
    public async Task ModelSetLocal_ClearsAStaleRemoteDimension()
    {
        var store = new FakeConfigStore();
        store.Settings[EmbeddingSettingsKeys.Dimensions] = "3072";

        await RunLocal(["model", "embedding", "set", "local"], store);

        store.Settings.ShouldNotContainKey(EmbeddingSettingsKeys.Dimensions,
            "a 384 local engine must not inherit the previous remote model's dimension");
    }

    [Fact]
    public async Task ModelReset_ClearsTheDimensionRowAlongsideTheOtherFive()
    {
        var store = new FakeConfigStore();
        store.Settings[EmbeddingSettingsKeys.Dimensions] = "1024";

        await CliRun.RunAsync(["settings", "model", "reset"],
            (_, streams, ct) => new SettingsCommands().ModelResetAsync(store, streams, ct));

        store.Settings.ShouldNotContainKey(EmbeddingSettingsKeys.Dimensions);
    }
}
