using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Launch-identity resolution after the single-channel refactor: transport, data root,
///     install scope and port come from CLI flags only (env handling removed); runtime
///     configuration lives in the settings table via config commands.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ServerConfigTests
{
    [Fact]
    public void ToServerConfig_DefaultCli_AppliesDefaults()
    {
        var config = Cli().ToServerConfig();

        config.Transport.ShouldBe(McpTransport.Proxy);
        config.Options.DataRoot.ShouldBe(DefaultOptions.DataRoot);
        config.Options.Scope.ShouldBe(InstallScope.User);
        config.Port.ShouldBe(DefaultOptions.Port);
    }

    [Fact]
    public void ToServerConfig_TransportFromFlag()
    {
        Cli(transport: "http").ToServerConfig().Transport.ShouldBe(McpTransport.Http);
        Cli(transport: "https").ToServerConfig().Transport.ShouldBe(McpTransport.Https);
        Cli(transport: "stdio").ToServerConfig().Transport.ShouldBe(McpTransport.Stdio);
        Cli().ToServerConfig().Transport.ShouldBe(McpTransport.Proxy);
    }

    /// <summary>
    ///     The only lever that reaches installed clients (ADR-0020): every declaration invokes the
    ///     binary bare, so the bare parse — not an opt-in flag — has to select the proxy.
    /// </summary>
    [Fact]
    public void BareLaunch_SelectsTheProxyTransport()
    {
        CliArgs.TryParse([], out var parsed).ShouldBeTrue();

        parsed!.Options.ToServerConfig().Transport.ShouldBe(McpTransport.Proxy);
    }

    /// <summary>The escape hatch of ADR-0020: --transport stdio keeps the complete in-process server.</summary>
    [Fact]
    public void ExplicitStdio_StaysStdio()
    {
        CliArgs.TryParse(["--transport", "stdio"], out var parsed).ShouldBeTrue();

        parsed!.Options.ToServerConfig().Transport.ShouldBe(McpTransport.Stdio);
    }

    [Fact]
    public void ToServerConfig_DataRootFromFlag()
    {
        var config = Cli(dataRoot: "/x").ToServerConfig();

        config.Options.DataRoot.ShouldBe("/x");
    }

    [Fact]
    public void ToServerConfig_ExpandsTildeInDataRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var config = Cli(dataRoot: "~/x").ToServerConfig();

        config.Options.DataRoot.ShouldBe(Path.Combine(home, "x"));
    }

    [Fact]
    public void ToServerConfig_WhitespaceDataRoot_FallsBackToDefault()
    {
        var config = Cli(dataRoot: " ").ToServerConfig();

        config.Options.DataRoot.ShouldBe(DefaultOptions.DataRoot);
    }

    [Fact]
    public void ToServerConfig_AppliesPortFromFlag()
    {
        Cli(port: 7721, isPortExplicit: true).ToServerConfig().Port.ShouldBe(7721);
        Cli(port: 0, isPortExplicit: true).ToServerConfig().Port.ShouldBe(0);
    }

    [Fact]
    public void ToServerConfig_InstallScopeFromFlag()
    {
        Cli(scope: InstallScope.Project).ToServerConfig().Options.Scope.ShouldBe(InstallScope.Project);
        Cli(scope: InstallScope.User).ToServerConfig().Options.Scope.ShouldBe(InstallScope.User);
        Cli().ToServerConfig().Options.Scope.ShouldBe(InstallScope.User);
    }

    [Fact]
    public void ToServerConfig_CarriesQuietFlag()
    {
        Cli(quiet: true).ToServerConfig().Options.Quiet.ShouldBeTrue();
        Cli().ToServerConfig().Options.Quiet.ShouldBeFalse();
    }

    [Fact]
    public void ServerConfig_IdleTimeout_DefaultsToZero()
    {
        var config = new ServerConfig(7721, McpTransport.Stdio, new InfrastructureOptions { DataRoot = "/x", Scope = InstallScope.User });

        config.IdleTimeout.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ToServerConfig_DoesNotSetIdleTimeout()
    {
        // R3: the 4h watchdog default is serve-only; bare launches keep Zero (watchdog off).
        Cli().ToServerConfig().IdleTimeout.ShouldBe(TimeSpan.Zero);
        Cli(transport: "http").ToServerConfig().IdleTimeout.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void DefaultOptions_IdleTimeout_IsFourHours() => DefaultOptions.IdleTimeout.ShouldBe(TimeSpan.FromHours(4));

    /// <summary>
    ///     A record's synthesised ToString prints every property, and for `serve` stderr is where all
    ///     logging goes — so one interpolated config in a log line or exception would put the loopback
    ///     secret on disk (ADR-0020).
    /// </summary>
    [Fact]
    public void ToString_DoesNotPrintTheMcpToken()
    {
        const string token = "loopback-secret-that-must-not-be-printed";
        var config = new ServerConfig(7721, McpTransport.Proxy,
            new InfrastructureOptions { DataRoot = "/x", Scope = InstallScope.User }) { McpToken = token };

        var printed = config.ToString();

        printed.ShouldNotContain(token);
        // Still a useful line: the launch identity an operator would read it for survives.
        printed.ShouldContain("7721");
    }

    private static RootCliOptions Cli(string? transport = null, string? dataRoot = null, InstallScope? scope = null,
        int port = DefaultOptions.Port, bool isPortExplicit = false, bool isTransportExplicit = false, bool quiet = false) =>
        new()
        {
            Transport = transport is null ? DefaultOptions.Transport : Enum.Parse<McpTransport>(transport, true),
            DataRoot = dataRoot ?? DefaultOptions.DataRoot,
            InstallScope = scope ?? DefaultOptions.InstallScope,
            Port = port,
            IsPortExplicit = isPortExplicit,
            IsTransportExplicit = isTransportExplicit,
            Quiet = quiet
        };
}
