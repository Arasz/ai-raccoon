using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Parser behavior for the ai-raccoon CLI surface (System.CommandLine 2.0.10 GA):
///     options, errors, help/version detection. Pinpoints the 2.0.10 idioms in use.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class CliArgsTests
{
    [Fact]
    public void Parse_ParsesDataRootOption()
    {
        var parsed = CliArgs.Parse(["--data-root", "/x"]);

        parsed.Options.ShouldNotBeNull();
        parsed.Options.DataRoot.ShouldBe("/x");
    }

    [Fact]
    public void Parse_ParsesEqualsSyntax()
    {
        var parsed = CliArgs.Parse(["--access-mode=ro"]);

        parsed.Options.ShouldNotBeNull();
        parsed.Options.AccessMode.ShouldBe("ro");
    }

    [Fact]
    public void Parse_UnknownOption_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--bogus"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem().ShouldContain("--bogus");
    }

    [Fact]
    public void Parse_UnknownSecretOption_ReturnsError()
    {
        // Secrets are never CLI options: the parser's unknown-option error is the defense.
        foreach (var args in new[] { new[] { "--sync-access-key", "x" }, new[] { "--db-passphrase", "x" } })
        {
            var parsed = CliArgs.Parse(args);

            parsed.Options.ShouldBeNull();
            parsed.Errors.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Parse_MissingValue_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--data-root"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_InvalidEnumValue_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--transport", "ftp"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem().ShouldContain("--transport");
    }

    [Fact]
    public void Parse_Help_ReturnsShowHelp()
    {
        var parsed = CliArgs.Parse(["--help"]);

        parsed.ShowHelp.ShouldBeTrue();
        parsed.ShowVersion.ShouldBeFalse();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_TransportHttps_ParsesToHttps()
    {
        // https is a declared enum member; the unsupported warning is emitted downstream.
        var parsed = CliArgs.Parse(["--transport", "https"]);

        parsed.Options.ShouldNotBeNull();
        parsed.Options.Transport.ShouldBe("https");
    }

    [Fact]
    public void Parse_HelpAndBogus_BehaviorPinned()
    {
        // Pinned against System.CommandLine 2.0.10: help wins over the unknown option.
        var parsed = CliArgs.Parse(["--help", "--bogus"]);

        parsed.ShowHelp.ShouldBeTrue();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_NoArgs_ReturnsNullOptions()
    {
        var parsed = CliArgs.Parse([]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldBeEmpty();
        parsed.ShowHelp.ShouldBeFalse();
    }

    [Fact]
    public void Parse_DuplicateOption_ReturnsError()
    {
        // Pinned against System.CommandLine 2.0.10: a repeated single-value option is a
        // parse error ("expects a single argument but 2 were provided"), not last-wins.
        var parsed = CliArgs.Parse(["--data-root", "/a", "--data-root", "/b"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem().ShouldContain("--data-root");
    }
}
