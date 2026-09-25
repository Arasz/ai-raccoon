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
    [InlineData("Cuda", EmbeddingDevice.Cuda)]
    [InlineData("coreml", EmbeddingDevice.CoreMl)]
    [InlineData(" CoreML ", EmbeddingDevice.CoreMl)]
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
    [InlineData(EmbeddingDevice.Cuda, true, true)]
    [InlineData(EmbeddingDevice.Cuda, false, true)]
    [InlineData(EmbeddingDevice.CoreMl, true, true)]
    [InlineData(EmbeddingDevice.CoreMl, false, false)]
    public void PrefersGpu_AutoMeansTheBundledEngineOnly(EmbeddingDevice device, bool bundled, bool expected) =>
        EmbeddingDeviceSetting.PrefersGpu(device, bundled).ShouldBe(expected);

    [Theory]
    [InlineData(EmbeddingDevice.Mlx, true, true)]
    [InlineData(EmbeddingDevice.Mlx, false, false)]
    [InlineData(EmbeddingDevice.Auto, true, false)]
    [InlineData(EmbeddingDevice.Gpu, true, false)]
    [InlineData(EmbeddingDevice.Cpu, true, false)]
    [InlineData(EmbeddingDevice.Cuda, true, false)]
    [InlineData(EmbeddingDevice.CoreMl, true, false)]
    [InlineData(EmbeddingDevice.CoreMl, false, false)]
    public void PrefersMlx_OnlyWhenExplicitlySetAndBundled(EmbeddingDevice device, bool bundled, bool expected) =>
        EmbeddingDeviceSetting.PrefersMlx(device, bundled).ShouldBe(expected);

    [Theory]
    [InlineData(EmbeddingDevice.CoreMl, true, true)]
    [InlineData(EmbeddingDevice.CoreMl, false, false)]
    [InlineData(EmbeddingDevice.Auto, true, false)]
    [InlineData(EmbeddingDevice.Auto, false, false)]
    [InlineData(EmbeddingDevice.Gpu, true, false)]
    [InlineData(EmbeddingDevice.Cpu, true, false)]
    [InlineData(EmbeddingDevice.Mlx, true, false)]
    [InlineData(EmbeddingDevice.Cuda, true, false)]
    public void PrefersCoreMl_OnlyWhenExplicitlySetAndBundled(EmbeddingDevice device, bool bundled, bool expected) =>
        EmbeddingDeviceSetting.PrefersCoreMl(device, bundled).ShouldBe(expected);

    [Theory]
    [InlineData(EmbeddingDevice.Cuda, "/opt/ort/libonnxruntime_providers_cuda.so", "/opt/ort/libonnxruntime_providers_cuda.so")]
    [InlineData(EmbeddingDevice.Cuda, null, "")]
    [InlineData(EmbeddingDevice.Gpu, "/opt/ort/libonnxruntime_providers_cuda.so", null)]
    [InlineData(EmbeddingDevice.Auto, "/opt/ort/libonnxruntime_providers_cuda.so", null)]
    [InlineData(EmbeddingDevice.Mlx, "/opt/ort/libonnxruntime_providers_cuda.so", null)]
    [InlineData(EmbeddingDevice.Cpu, "/opt/ort/libonnxruntime_providers_cuda.so", null)]
    public void CudaLibraryFor_IsReadOnlyForDeviceCuda_StaleLibraryIsInertOtherwise(EmbeddingDevice device, string? stored,
        string? expected) =>
        EmbeddingDeviceSetting.CudaLibraryFor(device, stored).ShouldBe(expected);
}
