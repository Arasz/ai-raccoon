using AiRaccoon.Tests.Unit.Retrieval;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Retrieval;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ReferenceAssetGateTests
{
    /// <summary>
    ///     Gate: the pinned reference assets must exist and verify after bootstrap. This FAILS
    ///     (never skips) when an asset is missing or mismatched — a false-green harness would let
    ///     the extension removal slip through without a reference oracle.
    /// </summary>
    [Fact]
    public async Task PinnedAssets_ArePresentAndShaVerified_AfterBootstrap()
    {
        var result = await ReferenceAssets.EnsureAsync(TestContext.Current.CancellationToken);

        result.Errors.ShouldBeEmpty(
            $"reference assets missing or mismatched; run the bootstrap with network access or place verified copies in ~/.ai-raccoon/extensions/{ReferenceAssets.CurrentPlatform}/ and ~/.ai-raccoon/models/");

        File.Exists(ReferenceAssets.MemoryModulePath).ShouldBeTrue();
        File.Exists(ReferenceAssets.VectorModulePath).ShouldBeTrue();
        File.Exists(ReferenceAssets.ModelPath).ShouldBeTrue();

        ReferenceAssets.Sha256Of(ReferenceAssets.MemoryModulePath)
            .ShouldBe(
                ReferenceAssets.ActiveAssets.Single(a => a.Repo == "sqlite-memory").Sha256,
                StringCompareShould.IgnoreCase);
        ReferenceAssets.Sha256Of(ReferenceAssets.VectorModulePath)
            .ShouldBe(
                ReferenceAssets.ActiveAssets.Single(a => a.Repo == "sqlite-vector").Sha256,
                StringCompareShould.IgnoreCase);
        ReferenceAssets.Sha256Of(ReferenceAssets.ModelPath)
            .ShouldBe(
                ReferenceAssets.ActiveAssets.Single(a => a.Name == ReferenceAssets.ModelFileName).Sha256,
                StringCompareShould.IgnoreCase);
    }
}
