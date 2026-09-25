using System.Text.Json;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>The decisions behind supplying NVIDIA's Vulkan ICD manifest when a container mounts the driver without it.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NvidiaVulkanIcdTests
{
    [Fact]
    public void ManifestDirectories_Defaults_FollowTheVulkanLoadersLinuxSearchOrder()
    {
        var dirs = NvidiaVulkanIcd.ManifestDirectories("/home/u", _ => null);

        dirs.ShouldBe([
            "/home/u/.config/vulkan/icd.d",
            "/etc/xdg/vulkan/icd.d",
            "/etc/vulkan/icd.d",
            "/home/u/.local/share/vulkan/icd.d",
            "/usr/local/share/vulkan/icd.d",
            "/usr/share/vulkan/icd.d",
        ]);
    }

    [Fact]
    public void ManifestDirectories_XdgVariables_ReplaceTheirDefaults()
    {
        var env = new Dictionary<string, string>
        {
            ["XDG_CONFIG_HOME"] = "/cfg",
            ["XDG_CONFIG_DIRS"] = "/c1:/c2",
            ["XDG_DATA_HOME"] = "/data",
            ["XDG_DATA_DIRS"] = "/d1",
        };

        var dirs = NvidiaVulkanIcd.ManifestDirectories("/home/u", name => env.GetValueOrDefault(name));

        dirs.ShouldBe([
            "/cfg/vulkan/icd.d", "/c1/vulkan/icd.d", "/c2/vulkan/icd.d", "/etc/vulkan/icd.d",
            "/data/vulkan/icd.d", "/d1/vulkan/icd.d",
        ]);
    }

    [Theory]
    [InlineData(false, "/lib/libGLX_nvidia.so.0", false, true)]
    [InlineData(true, "/lib/libGLX_nvidia.so.0", false, false)]
    [InlineData(false, null, false, false)]
    [InlineData(false, "/lib/libGLX_nvidia.so.0", true, false)]
    public void NeedsManifest_OnlyWhenTheDriverIsPresent_AndNothingElseTellsTheLoaderWhereItIs(
        bool anyManifestFound, string? driver, bool driverVariableSet, bool expected) =>
        NvidiaVulkanIcd.NeedsManifest(anyManifestFound, driver, driverVariableSet).ShouldBe(expected);

    [Fact]
    public void Manifest_NamesTheDriverLibrary_AsAVulkanIcd()
    {
        using var doc = JsonDocument.Parse(NvidiaVulkanIcd.Manifest("/usr/lib/x86_64-linux-gnu/libGLX_nvidia.so.0"));

        doc.RootElement.GetProperty("file_format_version").GetString().ShouldBe("1.0.1");
        doc.RootElement.GetProperty("ICD").GetProperty("library_path").GetString()
            .ShouldBe("/usr/lib/x86_64-linux-gnu/libGLX_nvidia.so.0");
    }
}
