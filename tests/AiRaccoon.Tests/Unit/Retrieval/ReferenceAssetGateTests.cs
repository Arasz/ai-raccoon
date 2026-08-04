using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReferenceAssetManifestTests
{
    [Fact]
    public void Manifest_PinsTheReferenceExtensionVersions()
    {
        var memory = ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.MemoryModuleName);
        memory.Repo.ShouldBe("sqlite-memory");
        memory.Version.ShouldBe("1.3.5");
        memory.AssetFile.ShouldBe("memory-osx-arm64-full-1.3.5.tar.gz");

        var vector = ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.VectorModuleName);
        vector.Repo.ShouldBe("sqlite-vector");
        vector.Version.ShouldBe("1.0.0");
        vector.AssetFile.ShouldBe("vector-osx-arm64-1.0.0.tar.gz");
    }

    [Fact]
    public void Manifest_PinsTheReferenceModelSha256FromDownloadScript()
    {
        var model = ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.ModelFileName);
        model.Kind.ShouldBe("model");
        model.Sha256.ShouldBe("908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3");
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
}

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ReferenceAssetGateTests
{
    /// <summary>
    ///     Gate: the pinned reference assets must exist and verify after bootstrap. This FAILS
    ///     (never skips) when an asset is missing or mismatched — a false-green harness would let
    ///     the P10 extension removal slip through without a reference oracle.
    /// </summary>
    [Fact]
    public async Task PinnedAssets_ArePresentAndShaVerified_AfterBootstrap()
    {
        var result = await ReferenceAssets.EnsureAsync(TestContext.Current.CancellationToken);

        result.Errors.ShouldBeEmpty(
            "reference assets missing or mismatched; run the bootstrap with network access or place " +
            "verified copies in ~/.ai-raccoon/extensions/osx-arm64/ and ~/.ai-raccoon/models/");

        File.Exists(ReferenceAssets.MemoryModulePath).ShouldBeTrue();
        File.Exists(ReferenceAssets.VectorModulePath).ShouldBeTrue();
        File.Exists(ReferenceAssets.ModelPath).ShouldBeTrue();

        ReferenceAssets.Sha256Of(ReferenceAssets.MemoryModulePath)
            .ShouldBe(
                ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.MemoryModuleName).Sha256,
                StringCompareShould.IgnoreCase);
        ReferenceAssets.Sha256Of(ReferenceAssets.VectorModulePath)
            .ShouldBe(
                ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.VectorModuleName).Sha256,
                StringCompareShould.IgnoreCase);
        ReferenceAssets.Sha256Of(ReferenceAssets.ModelPath)
            .ShouldBe(
                ReferenceAssets.PinnedAssets.Single(a => a.Name == ReferenceAssets.ModelFileName).Sha256,
                StringCompareShould.IgnoreCase);
    }
}
