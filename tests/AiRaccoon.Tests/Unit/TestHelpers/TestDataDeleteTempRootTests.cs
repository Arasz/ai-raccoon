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
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the lock is simulated with POSIX permission bits");
            return;
        }

        var root = TestData.CreateTempRoot("delete-temp-root-retry");
        File.WriteAllText(Path.Combine(root, "file.txt"), "content");

        // No write bit on the containing directory blocks unlink() of the entry inside it.
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var unlockAfter = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }, TestContext.Current.CancellationToken);

        TestData.DeleteTempRoot(root);

        await unlockAfter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public void PersistentlyLockedDirectory_ThrowsRatherThanSwallowingTheFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the lock is simulated with POSIX permission bits");
            return;
        }

        var root = TestData.CreateTempRoot("delete-temp-root-persistent-lock");
        File.WriteAllText(Path.Combine(root, "file.txt"), "content");
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Bounded retry means this returns in well under a second — it must not hang forever,
            // and it must not report success for a directory that is still there.
            Should.Throw<UnauthorizedAccessException>(() => TestData.DeleteTempRoot(root));
            Directory.Exists(root).ShouldBeTrue();
        }
        finally
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, true);
        }
    }
}
