using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReferenceAssetManifestTests
{
    private static readonly string[] SupportedPlatforms = ["osx-arm64", "linux-x64"];

    [Fact]
    public void Manifest_PinsTheReferenceExtensionVersionsForEverySupportedPlatform()
    {
        foreach (var platform in SupportedPlatforms)
        {
            var memory = ReferenceAssets.PinnedAssets.Single(a => a.Platform == platform && a.Repo == "sqlite-memory");
            memory.Kind.ShouldBe("extension");
            memory.Version.ShouldBe("1.3.5");
            memory.AssetFile.ShouldBe($"memory-{AssetFilePlatform(platform)}-full-1.3.5.tar.gz");
            memory.Url.ShouldContain(memory.AssetFile!);
            memory.Sha256.ShouldNotBeNullOrWhiteSpace();

            var vector = ReferenceAssets.PinnedAssets.Single(a => a.Platform == platform && a.Repo == "sqlite-vector");
            vector.Kind.ShouldBe("extension");
            vector.Version.ShouldBe("1.0.0");
            vector.AssetFile.ShouldBe($"vector-{AssetFilePlatform(platform)}-1.0.0.tar.gz");
            vector.Url.ShouldContain(vector.AssetFile!);
            vector.Sha256.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Manifest_PinsTheReferenceModelSha256FromDownloadScript()
    {
        var model = ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.ModelFileName);
        model.Kind.ShouldBe("model");
        model.Sha256.ShouldBe("908C82AC3849F9CA23158117CEC614BD8EC404040D8794C35B4C81242BF315E3", StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void Manifest_EveryAssetHasASha256AndUrl()
    {
        ReferenceAssets.PinnedAssets.ShouldNotBeEmpty();
        foreach (var asset in ReferenceAssets.PinnedAssets)
        {
            asset.Sha256.ShouldNotBeNullOrWhiteSpace();
            asset.Url.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Manifest_ModelAssetIsPlatformIndependent() => ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.ModelFileName).Platform.ShouldBeNull();

    private static string AssetFilePlatform(string platform) =>
        platform switch
        {
            "osx-arm64" => "macos-arm64",
            "linux-x64" => "linux-x86_64",
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
}

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReferenceAssetsPlatformTests
{
    [Fact]
    public void CurrentPlatform_ResolvesToASupportedPlatform() => ReferenceAssets.CurrentPlatform.ShouldBeOneOf("osx-arm64", "linux-x64");

    [Fact]
    public void ActiveAssets_ContainCurrentPlatformModulesAndTheModel()
    {
        var memory = ReferenceAssets.ActiveAssets.Single(a => a.Repo == "sqlite-memory");
        memory.Platform.ShouldBe(ReferenceAssets.CurrentPlatform);

        var vector = ReferenceAssets.ActiveAssets.Single(a => a.Repo == "sqlite-vector");
        vector.Platform.ShouldBe(ReferenceAssets.CurrentPlatform);

        ReferenceAssets.ActiveAssets.ShouldContain(a => a.Name == ReferenceAssets.ModelFileName);
    }

    [Fact]
    public void ModulePaths_ResolveInsideTheAssetsDirectory()
    {
        ReferenceAssets.MemoryModulePath.ShouldStartWith(ReferenceAssets.AssetsDirectory);
        ReferenceAssets.VectorModulePath.ShouldStartWith(ReferenceAssets.AssetsDirectory);
        ReferenceAssets.ModelPath.ShouldStartWith(ReferenceAssets.AssetsDirectory);
    }

    [Fact]
    public void ModuleNames_ArePlatformSpecific()
    {
        var extension = ReferenceAssets.CurrentPlatform.StartsWith("osx") ? ".dylib" : ".so";
        ReferenceAssets.MemoryModuleName.ShouldEndWith(extension);
        ReferenceAssets.VectorModuleName.ShouldEndWith(extension);
    }
}
