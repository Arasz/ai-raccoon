using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>ADR-0108: auto trusts the GPU only for the bundled fp16 engine; the setting can widen or disable it.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingDeviceSettingTests
{
    [Theory]
    [InlineData(null, EmbeddingDevice.Auto)]
    [InlineData("", EmbeddingDevice.Auto)]
    [InlineData("auto", EmbeddingDevice.Auto)]
    [InlineData(" GPU ", EmbeddingDevice.Gpu)]
    [InlineData("cpu", EmbeddingDevice.Cpu)]
    [InlineData(" MLX ", EmbeddingDevice.Mlx)]
    [InlineData("tpu", EmbeddingDevice.Auto)]
    public void Parse_ReadsTheStoredValue_UnknownIsAuto(string? raw, EmbeddingDevice expected) =>
        EmbeddingDeviceSetting.Parse(raw).ShouldBe(expected);

    [Theory]
    [InlineData(EmbeddingDevice.Auto, true, true)]
    [InlineData(EmbeddingDevice.Auto, false, false)]
    [InlineData(EmbeddingDevice.Gpu, false, true)]
    [InlineData(EmbeddingDevice.Cpu, true, false)]
    [InlineData(EmbeddingDevice.Mlx, true, true)]
    [InlineData(EmbeddingDevice.Mlx, false, false)]
    public void PrefersGpu_AutoMeansTheBundledEngineOnly(EmbeddingDevice device, bool bundled, bool expected) =>
        EmbeddingDeviceSetting.PrefersGpu(device, bundled).ShouldBe(expected);

    [Theory]
    [InlineData(EmbeddingDevice.Mlx, true, true)]
    [InlineData(EmbeddingDevice.Mlx, false, false)]
    [InlineData(EmbeddingDevice.Auto, true, false)]
    [InlineData(EmbeddingDevice.Gpu, true, false)]
    [InlineData(EmbeddingDevice.Cpu, true, false)]
    public void PrefersMlx_OnlyWhenExplicitlySetAndBundled(EmbeddingDevice device, bool bundled, bool expected) =>
        EmbeddingDeviceSetting.PrefersMlx(device, bundled).ShouldBe(expected);
}
