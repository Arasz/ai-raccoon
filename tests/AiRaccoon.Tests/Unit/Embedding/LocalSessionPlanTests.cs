using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0118 "Switching devices", layer 1: for every device value, bundled or custom model, on each
///     platform, which providers the ONNX session tries and whether the Neural Engine wraps it. CPU is
///     always the last resort, so it is not a column. The table is the contract; it is written out,
///     not derived.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LocalSessionPlanTests
{
    public static TheoryData<EmbeddingDevice, bool, string, LocalSessionPlan> Matrix => new()
    {
        { EmbeddingDevice.Auto, true, "osx-arm64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Auto, false, "osx-arm64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Gpu, true, "osx-arm64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Gpu, false, "osx-arm64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Cpu, true, "osx-arm64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Cpu, false, "osx-arm64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Mlx, true, "osx-arm64", new LocalSessionPlan(true, false, true, false, null) },
        { EmbeddingDevice.Mlx, false, "osx-arm64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Cuda, true, "osx-arm64", new LocalSessionPlan(false, true, true, false, null) },
        { EmbeddingDevice.Cuda, false, "osx-arm64", new LocalSessionPlan(false, true, true, false, null) },
        { EmbeddingDevice.CoreMl, true, "osx-arm64", new LocalSessionPlan(false, false, true, true, null) },
        { EmbeddingDevice.CoreMl, false, "osx-arm64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Auto, true, "linux-x64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Auto, false, "linux-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Gpu, true, "linux-x64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Gpu, false, "linux-x64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Cpu, true, "linux-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Cpu, false, "linux-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Mlx, true, "linux-x64", new LocalSessionPlan(true, false, true, false, null) },
        { EmbeddingDevice.Mlx, false, "linux-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Cuda, true, "linux-x64", new LocalSessionPlan(false, true, true, false, null) },
        { EmbeddingDevice.Cuda, false, "linux-x64", new LocalSessionPlan(false, true, true, false, null) },
        { EmbeddingDevice.CoreMl, true, "linux-x64", new LocalSessionPlan(false, false, true, false, "requires macOS on Apple Silicon") },
        { EmbeddingDevice.CoreMl, false, "linux-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Auto, true, "win-x64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Auto, false, "win-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Gpu, true, "win-x64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Gpu, false, "win-x64", new LocalSessionPlan(false, false, true, false, null) },
        { EmbeddingDevice.Cpu, true, "win-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Cpu, false, "win-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Mlx, true, "win-x64", new LocalSessionPlan(true, false, true, false, null) },
        { EmbeddingDevice.Mlx, false, "win-x64", new LocalSessionPlan(false, false, false, false, null) },
        { EmbeddingDevice.Cuda, true, "win-x64", new LocalSessionPlan(false, true, true, false, null) },
        { EmbeddingDevice.Cuda, false, "win-x64", new LocalSessionPlan(false, true, true, false, null) },
        { EmbeddingDevice.CoreMl, true, "win-x64", new LocalSessionPlan(false, false, true, false, "requires macOS on Apple Silicon") },
        { EmbeddingDevice.CoreMl, false, "win-x64", new LocalSessionPlan(false, false, false, false, null) },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void For_EveryDeviceModelAndPlatform_ChoosesThisPath(EmbeddingDevice device, bool bundled, string platform,
        LocalSessionPlan expected) =>
        LocalSessionPlan.For(device, bundled, platform == "osx-arm64", true).ShouldBe(expected);

    [Fact]
    public void Matrix_CoversEveryDeviceValue() =>
        Matrix.Select(row => (EmbeddingDevice)row.Data.Item1).Distinct().Order().ShouldBe(Enum.GetValues<EmbeddingDevice>().Order());

    [Fact]
    public void CoreMl_OnAppleSiliconWithoutTheGraph_KeepsWebGpu_AndSaysWhy() =>
        LocalSessionPlan.For(EmbeddingDevice.CoreMl, true, true, false)
            .ShouldBe(new LocalSessionPlan(false, false, true, false, "graph missing"));
}
