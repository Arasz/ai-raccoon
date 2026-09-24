using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     The file lookup that gates a WebGPU plugin attempt off macOS, and the process-wide plugin
///     registration guard, seamed off from a real ONNX Runtime so they run with temp files and fakes.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WebGpuPluginResolutionTests
{
    private static readonly string PluginFileName =
        OperatingSystem.IsWindows() ? "onnxruntime_providers_webgpu.dll" : "libonnxruntime_providers_webgpu.so";

    [Fact]
    public void ResolveWebGpuPluginLibrary_LibraryPresent_ReturnsItsPath()
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            var webGpuDirectory = Path.Combine(baseDirectory, "webgpu");
            Directory.CreateDirectory(webGpuDirectory);
            var library = Path.Combine(webGpuDirectory, PluginFileName);
            File.WriteAllText(library, "");

            OnnxEmbeddingGenerator.ResolveWebGpuPluginLibrary(baseDirectory).ShouldBe(library);
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Fact]
    public void ResolveWebGpuPluginLibrary_OnlyTheOtherPlatformsLibrary_ReturnsNull()
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            var webGpuDirectory = Path.Combine(baseDirectory, "webgpu");
            Directory.CreateDirectory(webGpuDirectory);
            var otherPlatforms = OperatingSystem.IsWindows() ? "libonnxruntime_providers_webgpu.so" : "onnxruntime_providers_webgpu.dll";
            File.WriteAllText(Path.Combine(webGpuDirectory, otherPlatforms), "");

            OnnxEmbeddingGenerator.ResolveWebGpuPluginLibrary(baseDirectory).ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Fact]
    public void ResolveWebGpuPluginLibrary_NoWebGpuSubdirectory_ReturnsNull()
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            OnnxEmbeddingGenerator.ResolveWebGpuPluginLibrary(baseDirectory).ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Fact]
    public void EnsureRegistered_SameNameAndPathTwice_RegistersOnce()
    {
        var name = UniqueName();
        var calls = new List<(string Name, string Path)>();

        OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/a.so", (n, p) => calls.Add((n, p))).ShouldBeNull();
        OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/a.so", (n, p) => calls.Add((n, p))).ShouldBeNull();

        calls.ShouldBe([(name, "/libs/a.so")]);
    }

    [Fact]
    public void EnsureRegistered_SameNameDifferentPath_IsRefused_WithoutRegistering()
    {
        var name = UniqueName();
        var calls = 0;
        OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/a.so", (_, _) => calls++);

        var refusal = OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/b.so", (_, _) => calls++);

        refusal.ShouldBe($"a different {name} provider library is already registered in this process (restart the server)");
        calls.ShouldBe(1);
    }

    [Fact]
    public void EnsureRegistered_FailedRegistration_IsNotRecorded()
    {
        var name = UniqueName();

        Should.Throw<DllNotFoundException>(() =>
            OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/broken.so", (_, _) => throw new DllNotFoundException("broken")));
        var calls = 0;
        var refusal = OnnxEmbeddingGenerator.EnsureRegistered(name, "/libs/fixed.so", (_, _) => calls++);

        refusal.ShouldBeNull();
        calls.ShouldBe(1);
    }

    private static string UniqueName() => $"TestExecutionProvider{Guid.NewGuid():N}";
}
