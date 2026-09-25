using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0113: capping MLX's free-buffer cache is best effort. A plugin directory without a
///     loadable <c>libmlxc.dylib</c> reports why and never throws, so the MLX session still runs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MlxCacheLimitTests
{
    [Fact]
    public void TryApply_NoMlxLibraryInTheDirectory_ReturnsFalseWithAReason()
    {
        var directory = TestData.CreateTempRoot();
        try
        {
            var applied = new MlxCacheLimit(NullLogger.Instance).TryApply(directory, MlxCacheLimit.DefaultBytes);

            applied.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TryApply_AFileThatIsNotALibrary_ReturnsFalseInsteadOfThrowing()
    {
        var directory = TestData.CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(directory, "libmlxc.dylib"), "not a Mach-O image");

            var applied = new MlxCacheLimit(NullLogger.Instance).TryApply(directory, MlxCacheLimit.DefaultBytes);

            applied.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DefaultBytes_Is512MiB() => MlxCacheLimit.DefaultBytes.ShouldBe(512L * 1024 * 1024);
}
