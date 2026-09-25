using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>The lookup that decides whether ORT loads the WebGPU core under webgpu/ (ADR-0115).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OnnxRuntimeCoreTests
{
    [Fact]
    public void ResolveWebGpuCore_CorePresentOnWindowsOrLinux_ReturnsItsPath()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Assert.Skip("only Windows and Linux packages ship a WebGPU core");
        }

        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            var core = Path.Combine(baseDirectory, "webgpu", OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so");
            Directory.CreateDirectory(Path.GetDirectoryName(core)!);
            File.WriteAllText(core, "");

            OnnxRuntimeCore.ResolveWebGpuCore(baseDirectory).ShouldBe(core);
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Fact]
    public void ResolveWebGpuCore_OnlyTheOtherPlatformsCore_ReturnsNull()
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            var other = Path.Combine(baseDirectory, "webgpu", OperatingSystem.IsWindows() ? "libonnxruntime.so" : "onnxruntime.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(other, "");

            OnnxRuntimeCore.ResolveWebGpuCore(baseDirectory).ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }

    [Fact]
    public void ResolveWebGpuCore_NoWebGpuDirectory_ReturnsNull()
    {
        var baseDirectory = TestData.CreateTempRoot();
        try
        {
            OnnxRuntimeCore.ResolveWebGpuCore(baseDirectory).ShouldBeNull();
        }
        finally
        {
            TestData.DeleteTempRoot(baseDirectory);
        }
    }
}
