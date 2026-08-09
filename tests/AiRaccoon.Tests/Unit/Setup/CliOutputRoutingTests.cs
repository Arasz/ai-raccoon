using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Stdout-corruption guard: all CLI text (help, version, parse errors) renders only to
///     the writer passed to Render — stdout stays reserved for stdio protocol frames. The
///     redirected-Console tests run serially since they mutate Console.Out.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(SerialCollectionName)]
public class CliOutputRoutingTests
{
    public const string SerialCollectionName = "CliOutputRouting-Serial";

    [Fact]
    public void Render_Help_WritesOnlyToErrorWriter()
    {
        CliArgs.TryParse(["--help"], out var parsed);
        var stderr = new StringWriter();

        parsed.RenderTo(stderr);

        stderr.ToString().ShouldContain("Usage");
    }

    [Fact]
    public void Render_ParseError_WritesOnlyToErrorWriter()
    {
        CliArgs.TryParse(["--bogus"], out var parsed);
        var stderr = new StringWriter();

        var exit = parsed.RenderTo(stderr);

        exit.ShouldBe(1);
        stderr.ToString().ShouldContain("Unrecognized command or argument '--bogus'.");
    }

    [Fact]
    public void Render_Version_WritesOnlyToErrorWriter()
    {
        // The rendered string is the entry assembly's version (the test host), so only the
        // routing contract is asserted here; VersionContractTests covers the actual version string.
        CliArgs.TryParse(["--version"], out var parsed);
        var stderr = new StringWriter();

        parsed.RenderTo(stderr);

        stderr.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_Help_ReturnsZeroExitCode()
    {
        CliArgs.TryParse(["--help"], out var parsed);

        parsed.RenderTo(new StringWriter()).ShouldBe(0);
    }

    [Fact]
    public void Render_Version_ReturnsZeroExitCode()
    {
        CliArgs.TryParse(["--version"], out var parsed);

        parsed.RenderTo(new StringWriter()).ShouldBe(0);
    }

    [Fact]
    public void Render_Help_NeverWritesToRealStdout()
    {
        var original = Console.Out;
        try
        {
            using var redirected = new StringWriter();
            Console.SetOut(redirected);
            CliArgs.TryParse(["--help"], out var parsed);

            parsed.RenderTo(new StringWriter());

            redirected.ToString().ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void Render_ParseError_NeverWritesToRealStdout()
    {
        var original = Console.Out;
        try
        {
            using var redirected = new StringWriter();
            Console.SetOut(redirected);
            CliArgs.TryParse(["--bogus"], out var parsed);

            parsed.RenderTo(new StringWriter());

            redirected.ToString().ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [CollectionDefinition(SerialCollectionName, DisableParallelization = true)]
    public sealed class SerialCollection;
}
