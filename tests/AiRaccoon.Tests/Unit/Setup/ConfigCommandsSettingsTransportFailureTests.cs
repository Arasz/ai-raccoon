using AiRaccoon.Core.Memory;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP7-T7: a settings command whose transport fails must exit with a code distinct from both
///     success and the generic <see cref="ExitCode.InvalidArgument" /> catch-all, naming why in
///     stderr without a doubled "ai-raccoon:" prefix (the exception messages already carry it).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ConfigCommandsSettingsTransportFailureTests
{
    [Fact]
    public async Task ServerRefusedException_ExitsWithADistinctCode_AndUnprefixedMessage()
    {
        var store = new ThrowingStore(new SettingsServerRefusedException("ai-raccoon: the settings server at http://127.0.0.1:1/ refused this credential"));
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands());

        var (exit, _, err) = await CliRun.RunAsync(["settings", "sweep", "show"], commands);

        exit.ShouldBe(ExitCode.SettingsServerRefused);
        err.Trim().ShouldBe("ai-raccoon: the settings server at http://127.0.0.1:1/ refused this credential");
    }

    [Fact]
    public async Task ServerUnavailableException_ExitsWithADistinctCode_AndUnprefixedMessage()
    {
        var store = new ThrowingStore(new SettingsServerUnavailableException("ai-raccoon: no settings server answered at http://127.0.0.1:1/"));
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands());

        var (exit, _, err) = await CliRun.RunAsync(["settings", "sweep", "show"], commands);

        exit.ShouldBe(ExitCode.SettingsServerUnavailable);
        err.Trim().ShouldBe("ai-raccoon: no settings server answered at http://127.0.0.1:1/");
    }

    private sealed class ThrowingStore(Exception toThrow) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            throw toThrow;
    }
}
