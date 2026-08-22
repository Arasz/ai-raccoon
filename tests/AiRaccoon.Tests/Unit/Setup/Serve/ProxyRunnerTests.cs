using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Shouldly;
using Xunit;
using ProxyRunner = AiRaccoon.Hosting.Proxy.ProxyRunner;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     ProxyRunner acceptance (ADR-0020): a port the proxy cannot dial is refused outright, because
///     spawning a backend on a port nobody can reach orphans it until the idle watchdog retires it;
///     an unpackaged host (the dotnet muxer) is refused before any dial, naming the manual `serve`.
///     The process path is an explicit input so neither verdict depends on how this test host runs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProxyRunnerTests : IDisposable
{
    private const string AppHost = "/opt/ai-raccoon/ai-raccoon";
    private const string DotnetHost = "/usr/local/share/dotnet/dotnet";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-proxy-runner");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Theory]
    [InlineData(0)] // "any free port": the spawned serve binds one the proxy can never learn
    [InlineData(70000)] // out of range: EndpointFor cannot even build the URI
    public async Task Run_WithAPortItCannotDial_RefusesWithoutSpawningABackend(int port)
    {
        var stderr = new StringWriter();
        var config = new ServerConfig(port, McpTransport.Proxy,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });

        var exit = await TestData.CreateProxyRunner().RunAsync(config, new StandardStreams(TextReader.Null, TextWriter.Null, stderr), AppHost, TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.ProxyBackendUnavailable);
        var message = stderr.ToString();
        message.ShouldContain("--port 0");
        // Names the supported way to get a random port instead.
        message.ShouldContain("serve --port 0");
        // Nothing was started: "serve exit" only ever appears once a backend has been spawned.
        message.ShouldNotContain("serve exit");
    }

    [Fact]
    public async Task Run_WhenTheProcessIsTheDotnetHost_RefusesNamingTheServeCommand_WithoutDialing()
    {
        var stderr = new StringWriter();
        var config = new ServerConfig(54240, McpTransport.Proxy,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });

        var exit = await TestData.CreateProxyRunner().RunAsync(config, new StandardStreams(TextReader.Null, TextWriter.Null, stderr), DotnetHost, TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.ProxyBackendUnavailable);
        var message = stderr.ToString();
        message.ShouldContain("dotnet host");
        message.ShouldContain("serve --port 54240");
        message.ShouldNotContain("serve exit");
    }
}
