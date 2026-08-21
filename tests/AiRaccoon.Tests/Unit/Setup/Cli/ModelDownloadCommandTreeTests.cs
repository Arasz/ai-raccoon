using System.CommandLine;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Pins the WP2 CLI surface (plan D4 rev 2): `model download &lt;repo-id&gt;` exposes
///     --revision/--file/--dir/--dry-run/--yes and — after the review round's m2 — NO --set:
///     downloading must never silently activate a model.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ModelDownloadCommandTreeTests
{
    private static Command ModelDownload() =>
        CliCommandTree.BuildFullRootCommand().Children.OfType<Command>().Single(c => c.Name == "model")
            .Children.OfType<Command>().Single(c => c.Name == "download");

    [Fact]
    public void ModelDownload_IsAChildOfTheModelFamily()
    {
        ModelDownload().Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ModelDownload_ExposesTheApprovedFlags()
    {
        var download = ModelDownload();
        var options = download.Children.OfType<Option>().Select(o => o.Name).ToList();

        options.ShouldContain("--revision");
        options.ShouldContain("--file");
        options.ShouldContain("--dir");
        options.ShouldContain("--dry-run");
        options.ShouldContain("--yes");
    }

    [Fact]
    public void ModelDownload_HasNoSetOption_AndDoesNotChainActivation()
    {
        // Review round m2 (plan D4): --set chaining was removed — download must not silently activate.
        var download = ModelDownload();

        download.Children.OfType<Option>().Select(o => o.Name).ShouldNotContain("--set");
        download.Description.ShouldNotBeNull().ShouldContain("Does NOT activate", Case.Sensitive);
    }

    [Fact]
    public void ModelDownload_RequiresARepoIdArgument()
    {
        var repoId = ModelDownload().Children.OfType<Argument>().Single(a => a.Name == "repo-id");

        repoId.Arity.MinimumNumberOfValues.ShouldBe(1);
    }

    [Fact]
    public void ModelDownload_FileOption_IsRepeatable()
    {
        var file = ModelDownload().Children.OfType<Option>().Single(o => o.Name == "--file");

        file.Arity.MaximumNumberOfValues.ShouldBeGreaterThan(1);
    }

    [Fact]
    public void ModelDownload_FileOption_ParsesRepeatedOccurrences()
    {
        var root = CliCommandTree.BuildFullRootCommand();
        var result = root.Parse(["model", "download", "org/repo", "--file", "a.onnx", "--file", "b.onnx"]);

        result.Errors.ShouldBeEmpty();
        result.GetValue<string[]>("--file").ShouldBe(["a.onnx", "b.onnx"]);
    }
}
