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

        config.Transport.ShouldBe(McpTransport.Stdio);
        config.Options.DataRoot.ShouldBe(DefaultOptions.DataRoot);
        config.Options.Scope.ShouldBe(InstallScope.User);
        config.Port.ShouldBe(DefaultOptions.Port);
    }

    [Fact]
    public void ToServerConfig_TransportFromFlag()
    {
        Cli(transport: "http").ToServerConfig().Transport.ShouldBe(McpTransport.Http);
        Cli(transport: "https").ToServerConfig().Transport.ShouldBe(McpTransport.Https);
        Cli().ToServerConfig().Transport.ShouldBe(McpTransport.Stdio);
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

    private static CliOptions Cli(string? transport = null, string? dataRoot = null, InstallScope? scope = null,
        int port = DefaultOptions.Port, bool isPortExplicit = false) =>
        new()
        {
            Transport = transport is null ? DefaultOptions.Transport : Enum.Parse<McpTransport>(transport, ignoreCase: true),
            DataRoot = dataRoot ?? DefaultOptions.DataRoot,
            InstallScope = scope ?? DefaultOptions.InstallScope,
            Port = port,
            IsPortExplicit = isPortExplicit
        };
}
