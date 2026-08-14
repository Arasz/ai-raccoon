using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Pins what DeleteTempRoot promises over a bare Directory.Delete: a directory already gone is
///     success rather than a spurious teardown failure, a transient lock is retried instead of
///     failing the first time, and a lock that never releases still surfaces — the helper must not
///     swallow every failure, or a real leak becomes invisible.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TestDataDeleteTempRootTests
{
    [Fact]
    public void MissingDirectory_DoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ai-raccoon-tests", Guid.NewGuid().ToString("N"));

        Should.NotThrow(() => TestData.DeleteTempRoot(missing));
        Directory.Exists(missing).ShouldBeFalse();
    }

    [Fact]
    public async Task TransientlyLockedDirectory_RetriesUntilItSucceeds()
    {
        var root = LockedRootOrSkip("delete-temp-root-retry");

        var unlockAfter = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
            Unlock(root);
        }, TestContext.Current.CancellationToken);

        TestData.DeleteTempRoot(root);

        await unlockAfter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public void PersistentlyLockedDirectory_ThrowsRatherThanSwallowingTheFailure()
    {
        var root = LockedRootOrSkip("delete-temp-root-persistent-lock");
        try
        {
            // Bounded retry means this returns in well under a second — it must not hang forever,
            // and it must not report success for a directory that is still there.
            Should.Throw<UnauthorizedAccessException>(() => TestData.DeleteTempRoot(root));
            Directory.Exists(root).ShouldBeTrue();
        }
        finally
        {
            Unlock(root);
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     A temp root whose contents cannot be unlinked, or a skip. The lock is simulated with
    ///     POSIX permission bits, and those do not bind for uid 0 — CI containers routinely run as
    ///     root, where the "locked" directory deletes on the first attempt and the test measures
    ///     nothing. Proven here rather than assumed: the helper writes a probe file back into the
    ///     directory it just made read-only, and only proceeds when that write is refused.
    /// </summary>
    private static string LockedRootOrSkip(string prefix)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("the lock is simulated with POSIX permission bits");
        }

        var root = TestData.CreateTempRoot(prefix);
        File.WriteAllText(Path.Combine(root, "file.txt"), "content");

        // No write bit on the containing directory blocks unlink() of the entry inside it.
        SetMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.WriteAllText(Path.Combine(root, "probe.txt"), "x");
        }
        catch (UnauthorizedAccessException)
        {
            return root;
        }

        Unlock(root);
        Directory.Delete(root, true);
        Assert.Skip("permission bits do not bind for this user (running as root?), so nothing is locked");
        return root;
    }

    private static void Unlock(string root)
    {
        if (Directory.Exists(root))
        {
            SetMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetMode(string root, UnixFileMode mode)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(root, mode);
        }
    }
}
