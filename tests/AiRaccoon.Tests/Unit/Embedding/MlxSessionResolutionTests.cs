using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0110: the file-presence checks that gate an MLX attempt, seamed off from the actual
///     ONNX Runtime session so they are unit-testable with plain temp directories.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MlxSessionResolutionTests
{
    private static readonly string[] RequiredPluginFiles =
        ["libonnxruntime_mlx_ep.dylib", "libmlx.dylib", "libmlxc.dylib", "mlx.metallib"];

    [Fact]
    public void ResolveMlxPluginDirectory_AllFourFilesPresent_ReturnsTheDirectory()
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            var mlxDirectory = Path.Combine(baseDirectory, "mlx");
            Directory.CreateDirectory(mlxDirectory);
            foreach (var file in RequiredPluginFiles)
            {
                File.WriteAllText(Path.Combine(mlxDirectory, file), "");
            }

            OnnxEmbeddingGenerator.ResolveMlxPluginDirectory(baseDirectory).ShouldBe(mlxDirectory);
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Theory]
    [InlineData("libonnxruntime_mlx_ep.dylib")]
    [InlineData("libmlx.dylib")]
    [InlineData("libmlxc.dylib")]
    [InlineData("mlx.metallib")]
    public void ResolveMlxPluginDirectory_OneFileMissing_ReturnsNull(string missingFile)
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            var mlxDirectory = Path.Combine(baseDirectory, "mlx");
            Directory.CreateDirectory(mlxDirectory);
            foreach (var file in RequiredPluginFiles.Where(f => f != missingFile))
            {
                File.WriteAllText(Path.Combine(mlxDirectory, file), "");
            }

            OnnxEmbeddingGenerator.ResolveMlxPluginDirectory(baseDirectory).ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Fact]
    public void ResolveMlxPluginDirectory_NoMlxSubdirectory_ReturnsNull()
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            OnnxEmbeddingGenerator.ResolveMlxPluginDirectory(baseDirectory).ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Fact]
    public void ResolveMlxGraphPath_GraphBesideModel_ReturnsIt()
    {
        var directory = TestData.CreateTempRoot();
        try
        {
            var modelPath = Path.Combine(directory, "model_fp16.onnx");
            File.WriteAllText(modelPath, "");
            var graphPath = Path.Combine(directory, "model_fp16_mlx.onnx");
            File.WriteAllText(graphPath, "");

            OnnxEmbeddingGenerator.ResolveMlxGraphPath(modelPath).ShouldBe(graphPath);
        }
        finally
        {
            TestData.DeleteTempRoot(directory);
        }
    }

    [Fact]
    public void ResolveMlxGraphPath_NoGraphBesideModel_ReturnsNull()
    {
        var directory = TestData.CreateTempRoot();
        try
        {
            var modelPath = Path.Combine(directory, "model_fp16.onnx");
            File.WriteAllText(modelPath, "");

            OnnxEmbeddingGenerator.ResolveMlxGraphPath(modelPath).ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(directory);
        }
    }
}
