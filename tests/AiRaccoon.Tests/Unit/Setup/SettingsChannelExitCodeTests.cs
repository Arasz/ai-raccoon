using System.Net;
using AiRaccoon.Core.Memory;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Delta review C2 (surface F3): a server-side 5xx on the settings channel must exit with a
///     code distinct from <see cref="ExitCode.InvalidArgument" /> ("you mistyped") — the server
///     broke, not the caller. Drives the real <see cref="ServerSettingsStore" /> (its
///     <c>Ensure</c>/<c>SendAsync</c> path) through <see cref="ConfigCommands" /> at argv level,
///     against a stubbed HTTP channel, the same shape <c>ServerProbeResilienceTests</c> uses.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SettingsChannelExitCodeTests
{
    [Fact]
    public async Task AServerSide500_ExitsWithItsOwnCode()
    {
        var serverStore = new ServerSettingsStore(
            new HttpClient(new StubHandler(HttpStatusCode.InternalServerError))
            {
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            "test-token");
        var commands = TestData.CreateConfigCommands(new SettingsRoutedStore(serverStore), settings: new SettingsCommands());

        var (exit, _, err) = await CliRun.RunAsync(["settings", "sweep", "show"], commands);

        exit.ShouldBe(ExitCode.SettingsServerError);
        err.ShouldContain("500");
        err.ShouldNotContain("you mistyped");
    }

    /// <summary>A genuine usage error must stay untouched by the new mapping.</summary>
    [Fact]
    public async Task AGenuineBadArgument_StillExitsWithInvalidArgument()
    {
        var (exit, _, err) = await CliRun.RunAsync(["settings", "retrieval", "alpha", "set", "abc"],
            TestData.CreateConfigCommands(new FakeConfigStore(), settings: new SettingsCommands()));

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldNotBeEmpty();
    }

    /// <summary>Routes settings calls to the injected <see cref="ISettingsStore" /> exactly like
    /// <c>LazyServerSettingsStore</c> does in production — the fake stands in for that indirection.</summary>
    private sealed class SettingsRoutedStore(ISettingsStore inner) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.GetSettingAsync(key, cancellationToken);
    }

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
