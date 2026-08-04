using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Stdout-corruption guard: all CLI text (help, version, parse errors) renders only to
///     the writer passed to Render — stdout stays reserved for stdio protocol frames.
///     The redirected-Console tests must not run in parallel with other tests that write
///     to Console.Out, hence the serial collection.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(SerialCollectionName)]
public class CliOutputRoutingTests
{
    public const string SerialCollectionName = "CliOutputRouting-Serial";

    [CollectionDefinition(SerialCollectionName, DisableParallelization = true)]
    public sealed class SerialCollection;

    [Fact]
    public void Render_Help_WritesOnlyToErrorWriter()
    {
        var parsed = CliArgs.Parse(["--help"]);
        var stderr = new StringWriter();

        CliArgs.Render(parsed, stderr);

        stderr.ToString().ShouldContain("Usage");
    }

    [Fact]
    public void Render_ParseError_WritesOnlyToErrorWriter()
    {
        var parsed = CliArgs.Parse(["--bogus"]);
        var stderr = new StringWriter();

        var exit = CliArgs.Render(parsed, stderr);

        exit.ShouldBe(1);
        stderr.ToString().ShouldContain("Unrecognized command or argument '--bogus'.");
    }

    [Fact]
    public void Render_Version_WritesOnlyToErrorWriter()
    {
        // The rendered string is the entry assembly's version (the test host here), so only
        // the routing contract is asserted; the tool's own 0.1.0-beta is proven by the smoke gate.
        var parsed = CliArgs.Parse(["--version"]);
        var stderr = new StringWriter();

        CliArgs.Render(parsed, stderr);

        stderr.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_Help_ReturnsZeroExitCode()
    {
        var parsed = CliArgs.Parse(["--help"]);

        CliArgs.Render(parsed, new StringWriter()).ShouldBe(0);
    }

    [Fact]
    public void Render_Version_ReturnsZeroExitCode()
    {
        var parsed = CliArgs.Parse(["--version"]);

        CliArgs.Render(parsed, new StringWriter()).ShouldBe(0);
    }

    [Fact]
    public void Render_Help_NeverWritesToRealStdout()
    {
        var original = Console.Out;
        try
        {
            using var redirected = new StringWriter();
            Console.SetOut(redirected);
            var parsed = CliArgs.Parse(["--help"]);

            CliArgs.Render(parsed, new StringWriter());

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
            var parsed = CliArgs.Parse(["--bogus"]);

            CliArgs.Render(parsed, new StringWriter());

            redirected.ToString().ShouldBeEmpty();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
