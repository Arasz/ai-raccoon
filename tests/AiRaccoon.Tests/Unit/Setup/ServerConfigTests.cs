using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Precedence merge: CLI args &gt; env vars &gt; built-in defaults, with ~ expansion on
///     the two path options and null-or-whitespace treated as unset for them.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ServerConfigTests
{
    private static ServerConfig Build(CliOptions? cli, params (string Key, string? Value)[] env) =>
        ServerConfig.Build(cli, key => env.FirstOrDefault(e => e.Key == key).Value);

    [Fact]
    public void Build_CliOverridesEnv()
    {
        var cli = new CliOptions(Transport: "http", DataRoot: "/cli", null, null, null, null, null, null, null);

        var config = Build(cli, ("MCP_TRANSPORT", "stdio"), ("AIRACCOON_DATA_ROOT", "/env"));

        config.Transport.ShouldBe(McpTransport.Http);
        config.Options.DataRoot.ShouldBe("/cli");
    }

    [Fact]
    public void Build_EnvUsedWhenCliAbsent()
    {
        var config = Build(null, ("MCP_TRANSPORT", "http"), ("AIRACCOON_DATA_ROOT", "/env"));

        config.Transport.ShouldBe(McpTransport.Http);
        config.Options.DataRoot.ShouldBe("/env");
    }

    [Fact]
    public void Build_DefaultWhenBothAbsent()
    {
        var config = Build(null);

        config.Transport.ShouldBe(McpTransport.Stdio);
        config.Options.DataRoot.ShouldBe(InfrastructureOptions.DefaultDataRoot());
        config.Options.Scope.ShouldBe(InstallScope.User);
        config.Options.AccessMode.ShouldBeNull();
        config.Options.EmbeddingModelPath.ShouldBeNull();
    }

    [Fact]
    public void Build_TransportCoercesEnvironmentValue()
    {
        Build(null, ("MCP_TRANSPORT", "http")).Transport.ShouldBe(McpTransport.Http);
        Build(null, ("MCP_TRANSPORT", "bogus")).Transport.ShouldBe(McpTransport.Stdio);
    }

    [Fact]
    public void Build_TransportPreservesHttpsEnvValue()
    {
        Build(null, ("MCP_TRANSPORT", "https")).Transport.ShouldBe(McpTransport.Https);
    }

    [Fact]
    public void Build_WhitespaceEnvFallsBackToDefault()
    {
        var config = Build(null,
            ("AIRACCOON_DATA_ROOT", " "),
            ("AIRACCOON_EMBEDDING_MODEL", " "));

        config.Options.DataRoot.ShouldBe(InfrastructureOptions.DefaultDataRoot());
        config.Options.EmbeddingModelPath.ShouldBeNull();
    }

    [Fact]
    public void Build_WhitespaceCliValueTreatedAsUnset()
    {
        var cli = new CliOptions(null, "", null, null, "", null, null, null, null);

        var config = Build(cli);

        config.Options.DataRoot.ShouldBe(InfrastructureOptions.DefaultDataRoot());
        config.Options.EmbeddingModelPath.ShouldBeNull();
    }

    [Fact]
    public void Build_ExpandsTildeInPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cli = new CliOptions(null, "~/x", null, null, "~/model.onnx", null, null, null, null);

        var config = Build(cli);

        config.Options.DataRoot.ShouldBe(Path.Combine(home, "x"));
        config.Options.EmbeddingModelPath.ShouldBe(Path.Combine(home, "model.onnx"));
    }

    [Fact]
    public void Build_SyncFieldsMergeIndependently()
    {
        var cli = new CliOptions(null, null, null, null, null, "https://s3.example.com", null, null, null);

        var config = Build(cli, ("AIRACCOON_SYNC_BUCKET", "memories"));

        config.Options.Sync.Endpoint.ShouldBe("https://s3.example.com");
        config.Options.Sync.Bucket.ShouldBe("memories");
        config.Options.Sync.Region.ShouldBeNull();
        config.Options.Sync.ObjectKey.ShouldBeNull();
    }

    [Fact]
    public void Build_SecretVarsNeverMerged()
    {
        var queried = new List<string>();
        ServerConfig.Build(null, key =>
        {
            queried.Add(key);
            return null;
        });

        queried.ShouldNotContain("AIRACCOON_OPENAI_API_KEY");
        queried.ShouldNotContain("AIRACCOON_SYNC_ACCESS_KEY");
        queried.ShouldNotContain("AIRACCOON_SYNC_SECRET_KEY");
        queried.ShouldNotContain("AIRACCOON_DB_PASSPHRASE");
    }

    [Fact]
    public void Build_AccessModeAndEmbeddingModelPassThrough()
    {
        var cli = new CliOptions(null, null, null, "ro", "/models/custom.onnx", null, null, null, null);

        var config = Build(cli);

        config.Options.AccessMode.ShouldBe("ro");
        config.Options.EmbeddingModelPath.ShouldBe("/models/custom.onnx");
    }
}
