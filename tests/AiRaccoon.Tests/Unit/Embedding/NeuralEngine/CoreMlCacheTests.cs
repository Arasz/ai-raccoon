using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.NeuralEngine;

/// <summary>
///     ADR-0118: the compiled-model cache is keyed by both graph and weights, is shared safely
///     between processes through a lock file, and never deletes a set another holder is using or
///     anything outside its own root.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CoreMlCacheTests : IDisposable
{
    private const string Key = "abcdef012345";
    private const string Ort = "1.30.0";
    private readonly string _root = TestData.CreateTempRoot("coreml-cache");

    public void Dispose() => TestData.DeleteTempRoot(_root);

    private string CacheRoot => Path.Combine(_root, "coreml-cache");

    private string SetDirectory => Path.Combine(CacheRoot, Key, Ort);

    [Fact]
    public void Key_ChangesWithTheGraphSha_AndWithTheWeightsSha()
    {
        var baseline = CoreMlCache.KeyFor("aa11", "bb22");

        CoreMlCache.KeyFor("aa12", "bb22").ShouldNotBe(baseline);
        CoreMlCache.KeyFor("aa11", "bb23").ShouldNotBe(baseline);
        baseline.Length.ShouldBe(12);
        baseline.ShouldMatch("^[0-9a-f]{12}$");
    }

    [Fact]
    public void BucketDirectory_IsPerBucket_UnderTheShaAndOrtVersion()
    {
        var cache = Cache();

        cache.BucketDirectory(512).ShouldBe(Path.Combine(SetDirectory, "CPUAndNeuralEngine", "bucket-512"));
    }

    [Fact]
    public void Acquire_WithoutTheMarker_DeletesTheHalfWrittenSet_AndCompiles()
    {
        var cache = Cache();
        var stale = Path.Combine(cache.BucketDirectory(256), "half-written.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "x");

        using var lease = cache.Acquire(TestContext.Current.CancellationToken);

        lease.Compiling.ShouldBeTrue();
        File.Exists(stale).ShouldBeFalse();
    }

    [Fact]
    public void Acquire_WithTheMarker_LoadsWarm_AndKeepsTheSet()
    {
        var cache = Cache();
        var compiled = Path.Combine(cache.BucketDirectory(256), "compiled.bin");
        using (var first = cache.Acquire(TestContext.Current.CancellationToken))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(compiled)!);
            File.WriteAllText(compiled, "x");
            first.MarkComplete();
        }

        using var lease = cache.Acquire(TestContext.Current.CancellationToken);

        lease.Compiling.ShouldBeFalse();
        File.Exists(compiled).ShouldBeTrue();
    }

    [Fact]
    public void Acquire_WhileAnotherHolderCompiles_WaitsAndLeavesTheirSetAlone()
    {
        var cache = Cache();
        var theirs = Path.Combine(cache.BucketDirectory(256), "in-progress.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(theirs)!);
        File.WriteAllText(theirs, "x");
        using var other = HoldLock(SetDirectory, FileShare.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

        Should.Throw<OperationCanceledException>(() => cache.Acquire(cancel.Token));

        File.Exists(theirs).ShouldBeTrue();
    }

    [Fact]
    public async Task Acquire_AfterTheOtherHolderFinishes_LoadsTheirResultWarm()
    {
        var cache = Cache();
        var other = HoldLock(SetDirectory, FileShare.None);
        var acquire = Task.Run(() => cache.Acquire(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(SetDirectory, CoreMlCache.CompleteMarkerName), "", TestContext.Current.CancellationToken);
        await other.DisposeAsync();

        using var lease = await acquire.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        lease.Compiling.ShouldBeFalse();
    }

    [Fact]
    public void MarkComplete_KeepsASharedLock_ThatBlocksAnExclusiveOne()
    {
        var cache = Cache();
        using var lease = cache.Acquire(TestContext.Current.CancellationToken);
        lease.MarkComplete();

        File.Exists(Path.Combine(SetDirectory, CoreMlCache.CompleteMarkerName)).ShouldBeTrue();
        Should.Throw<IOException>(() => HoldLock(SetDirectory, FileShare.None).Dispose());
        HoldLock(SetDirectory, FileShare.Read).Dispose();
    }

    [Fact]
    public void Prune_DeletesOtherShaAndOrtSets_ButKeepsTheCurrentOne()
    {
        var cache = Cache();
        var otherSha = Seed(Path.Combine(CacheRoot, "000000000000", Ort));
        var otherOrt = Seed(Path.Combine(CacheRoot, Key, "1.29.0"));
        var current = Seed(SetDirectory);

        cache.Prune();

        Directory.Exists(otherSha).ShouldBeFalse();
        Directory.Exists(Path.Combine(CacheRoot, "000000000000")).ShouldBeFalse();
        Directory.Exists(otherOrt).ShouldBeFalse();
        File.Exists(Path.Combine(current, "compiled.bin")).ShouldBeTrue();
    }

    [Fact]
    public void Prune_LeavesASetAnotherHolderHasLocked()
    {
        var cache = Cache();
        var inUse = Seed(Path.Combine(CacheRoot, "111111111111", Ort));
        using var holder = HoldLock(inUse, FileShare.Read);

        cache.Prune();

        File.Exists(Path.Combine(inUse, "compiled.bin")).ShouldBeTrue();
    }

    [Fact]
    public void Prune_NeverFollowsALinkOutOfTheCacheRoot()
    {
        var cache = Cache();
        var outside = Seed(Path.Combine(_root, "outside", Ort));
        Directory.CreateDirectory(CacheRoot);
        var link = Path.Combine(CacheRoot, "222222222222");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_root, "outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"symbolic links are unavailable here: {ex.Message}");
        }

        cache.Prune();

        File.Exists(Path.Combine(outside, "compiled.bin")).ShouldBeTrue();
    }

    [Fact]
    public void SizeBytes_CountsEveryFileUnderTheCacheRoot()
    {
        var cache = Cache();
        Directory.CreateDirectory(cache.BucketDirectory(256));
        File.WriteAllBytes(Path.Combine(cache.BucketDirectory(256), "a.bin"), new byte[1000]);
        Directory.CreateDirectory(cache.BucketDirectory(512));
        File.WriteAllBytes(Path.Combine(cache.BucketDirectory(512), "b.bin"), new byte[24]);

        CoreMlCache.SizeBytes(CacheRoot).ShouldBe(1024);
    }

    [Fact]
    public void SizeBytes_IsZeroWithoutACache() => CoreMlCache.SizeBytes(CacheRoot).ShouldBe(0);

    [Fact]
    public void ReadStatus_ReturnsWhatWriteStatusWrote()
    {
        var at = new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
        Cache().WriteStatus(new NeuralEngineTransition(NeuralEngineState.CompilingNeuralEngine, NeuralEngineState.Refused,
            NeuralEngineTrigger.TimedOut, "did not load within 5 minutes", at));

        CoreMlCache.ReadStatus(CacheRoot).ShouldBe(new CoreMlStatus("Refused", "TimedOut", "did not load within 5 minutes", at,
            Environment.ProcessId));
    }

    [Fact]
    public void ReadStatus_IsNullWithoutAStatusFile() => CoreMlCache.ReadStatus(CacheRoot).ShouldBeNull();

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"state\": \"Refused\"}")]
    public void ReadStatus_ThrowsInvalidData_ForAMalformedFile(string content)
    {
        Directory.CreateDirectory(CacheRoot);
        File.WriteAllText(Path.Combine(CacheRoot, CoreMlCache.StatusFileName), content);

        Should.Throw<InvalidDataException>(() => CoreMlCache.ReadStatus(CacheRoot));
    }

    private CoreMlCache Cache() => new(CacheRoot, Key, Ort, NullLogger.Instance);

    private static string Seed(string setDirectory)
    {
        var bucket = Path.Combine(setDirectory, "CPUAndNeuralEngine", "bucket-256");
        Directory.CreateDirectory(bucket);
        File.WriteAllText(Path.Combine(setDirectory, "compiled.bin"), "x");
        File.WriteAllText(Path.Combine(bucket, "compiled.bin"), "x");
        return setDirectory;
    }

    private static FileStream HoldLock(string setDirectory, FileShare share)
    {
        Directory.CreateDirectory(setDirectory);
        return new FileStream(Path.Combine(setDirectory, CoreMlCache.LockFileName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, share);
    }
}
