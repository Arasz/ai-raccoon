using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Launch-identity resolution after the single-channel refactor: transport,
///     data root and install scope come from CLI flags only (env handling removed);
///     runtime configuration lives in the settings table via config commands.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ServerConfigTests
{
    [Fact]
    public void Build_NullCli_AppliesDefaults()
    {
        var config = ServerConfig.Build(null);

        config.Transport.ShouldBe(McpTransport.Stdio);
        config.Options.DataRoot.ShouldBe(InfrastructureOptions.DefaultDataRoot());
        config.Options.Scope.ShouldBe(InstallScope.User);
    }

    [Fact]
    public void Build_TransportFromFlag()
    {
        ServerConfig.Build(new CliOptions("http", null, null)).Transport.ShouldBe(McpTransport.Http);
        ServerConfig.Build(new CliOptions("https", null, null)).Transport.ShouldBe(McpTransport.Https);
        ServerConfig.Build(new CliOptions("bogus", null, null)).Transport.ShouldBe(McpTransport.Stdio);
        ServerConfig.Build(new CliOptions(null, null, null)).Transport.ShouldBe(McpTransport.Stdio);
    }

    [Fact]
    public void Build_DataRootFromFlag()
    {
        var config = ServerConfig.Build(new CliOptions(null, "/x", null));

        config.Options.DataRoot.ShouldBe("/x");
    }

    [Fact]
    public void Build_ExpandsTildeInDataRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var config = ServerConfig.Build(new CliOptions(null, "~/x", null));

        config.Options.DataRoot.ShouldBe(Path.Combine(home, "x"));
    }

    [Fact]
    public void Build_WhitespaceDataRoot_FallsBackToDefault()
    {
        var config = ServerConfig.Build(new CliOptions(null, " ", null));

        config.Options.DataRoot.ShouldBe(InfrastructureOptions.DefaultDataRoot());
    }

    [Fact]
    public void Build_InstallScopeFromFlag()
    {
        ServerConfig.Build(new CliOptions(null, null, InstallScope.Project)).Options.Scope.ShouldBe(InstallScope.Project);
        ServerConfig.Build(new CliOptions(null, null, InstallScope.User)).Options.Scope.ShouldBe(InstallScope.User);
        ServerConfig.Build(new CliOptions(null, null, null)).Options.Scope.ShouldBe(InstallScope.User);
    }
}
