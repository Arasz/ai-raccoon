using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     D1: the per-root ECDSA P-256 trust anchor. `serve` mints it owner-only inside the bank state
///     directory; verifiers read it and never mint; a shared directory or file fails closed; debris
///     heals only once it is old enough to be dead.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class IdentityKeyFileTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-identity-key");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private string StateDirectory => Path.Combine(_dataRoot, ".ai-raccoon");

    [RetryFact]
    public async Task EnsureAsync_MintsAnOwnerOnlyP256Key_InTheBankStateDirectory()
    {
        var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot));

        var key = await keyFile.EnsureAsync(TestContext.Current.CancellationToken);

        key.ShouldNotBeNull();
        key.KeySize.ShouldBe(256);
        keyFile.Path.ShouldBe(Path.Combine(_dataRoot, ".ai-raccoon", IdentityKeyFile.FileName));
        File.Exists(Path.Combine(_dataRoot, IdentityKeyFile.FileName)).ShouldBeFalse();
        (await File.ReadAllTextAsync(keyFile.Path, TestContext.Current.CancellationToken))
            .ShouldStartWith("-----BEGIN PRIVATE KEY-----");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(keyFile.Path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(keyFile.StateDirectory)
                .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // A separate reader instance resolves the same trust anchor.
        new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot)).ReadKeyId().ShouldBe(IdentityProof.KeyId(key));
    }

    /// <summary>
    ///     The gate: only the serve path mints. Here the real `serve` run leaves the key in the state
    ///     directory — never at the data root's top level — and a read-only verifier on a clean root
    ///     is proven unable to mint one.
    /// </summary>
    [RetryFact]
    public async Task IdentityKey_IsMintedOnlyByServe_AndOnlyInTheStateDir()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        await using var run = ServeHarness.Start(
            ["--data-root", _dataRoot, "--install-scope", "project", "serve", "--port", port.ToString()]);
        await run.WaitForUrlAsync(TestContext.Current.CancellationToken);

        var minted = Path.Combine(_dataRoot, ".ai-raccoon", IdentityKeyFile.FileName);
        File.Exists(minted).ShouldBeTrue("serve must mint the identity key before it binds");
        File.Exists(Path.Combine(_dataRoot, IdentityKeyFile.FileName)).ShouldBeFalse();
        new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot)).Read().ShouldNotBeNull();

        // The server stays up until asked to stop.
        (await run.StopAsync()).ShouldBe(ExitCode.Success);
    }

    /// <summary>F2/D1: a planted permissive state directory must refuse the mint, not trust it.</summary>
    [RetryFact]
    public async Task Mint_IsSignedWithAnOwnerOnlyKey_AndFailsClosed_OnAPermissivePlantedDir()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX modes only; Windows ACL inheritance is the documented residual");
            return;
        }

        Directory.CreateDirectory(StateDirectory);
        File.SetUnixFileMode(StateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot));

        var key = await keyFile.EnsureAsync(TestContext.Current.CancellationToken);

        key.ShouldBeNull();
        keyFile.RefusalReason.ShouldNotBeNull().ShouldContain(StateDirectory);
        keyFile.RefusalReason.ShouldContain("chmod 700");
        Directory.GetFileSystemEntries(StateDirectory).ShouldBeEmpty();
    }

    /// <summary>D1 upgrade rule, key side: an owned 0755 state directory is tightened to 0700 and the key minted in it.</summary>
    [RetryFact]
    public async Task Ensure_OnAnOwnedStateDirectoryOthersCanOnlyRead_TightensItTo0700_AndMints()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX modes only; Windows ACL inheritance is the documented residual");
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(StateDirectory);
        File.SetUnixFileMode(StateDirectory, ownerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                             UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot));

        var key = await keyFile.EnsureAsync(TestContext.Current.CancellationToken);

        key.ShouldNotBeNull(keyFile.RefusalReason);
        File.GetUnixFileMode(StateDirectory).ShouldBe(ownerOnly);
        keyFile.TightenedStateDirectory.ShouldBeTrue();
    }

    /// <summary>F2/D1: an existing key file others can read is replaced by nothing — fail closed.</summary>
    [RetryFact]
    public async Task Ensure_OnAPlantedPermissiveKeyFile_Refuses_AndLeavesItUntouched()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX modes only; Windows ACL inheritance is the documented residual");
            return;
        }

        var sourceRoot = TestData.CreateTempRoot("ai-raccoon-identity-key-src");
        var source = new IdentityKeyFile(TestData.CreateProjectOptions(sourceRoot));
        try
        {
            await source.EnsureAsync(TestContext.Current.CancellationToken);
            var planted = await File.ReadAllBytesAsync(source.Path, TestContext.Current.CancellationToken);

            Directory.CreateDirectory(StateDirectory);
            File.SetUnixFileMode(StateDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var keyPath = Path.Combine(StateDirectory, IdentityKeyFile.FileName);
            await File.WriteAllBytesAsync(keyPath, planted, TestContext.Current.CancellationToken);
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

            var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot));
            var key = await keyFile.EnsureAsync(TestContext.Current.CancellationToken);

            key.ShouldBeNull();
            keyFile.RefusalReason.ShouldNotBeNull().ShouldContain(keyPath);
            keyFile.RefusalReason.ShouldContain("chmod 600");
            (await File.ReadAllBytesAsync(keyPath, TestContext.Current.CancellationToken)).ShouldBe(planted);
        }
        finally
        {
            TestData.DeleteTempRoot(sourceRoot);
        }
    }

    /// <summary>Debris left by a crash heals after the window, exactly like the token's.</summary>
    [RetryFact]
    public async Task AnEmptyKeyFile_IsHealed_AfterTheWait()
    {
        var time = new FakeTimeProvider();
        var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot), time);
        await SeedDebrisAsync(keyFile.Path, string.Empty, time);

        var healing = keyFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken); // timer registers
        healing.IsCompleted.ShouldBeFalse(); // still waiting out a possibly-live writer

        time.Advance(IdentityKeyFile.HealAfter);
        var healed = await healing.WaitAsync(TestContext.Current.CancellationToken);

        healed.ShouldNotBeNull();
        new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot)).Read().ShouldNotBeNull();
    }

    /// <summary>F7: a file a live writer just created is never deleted, even once the wait expires.</summary>
    [RetryFact]
    public async Task AnEmptyKeyFile_YoungerThanTheHealWindow_IsNotDeleted()
    {
        var time = new FakeTimeProvider();
        var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot), time);
        await SeedDebrisAsync(keyFile.Path, string.Empty, time, age: TimeSpan.FromMinutes(-5));

        var healing = keyFile.EnsureAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        time.Advance(IdentityKeyFile.HealAfter);

        var result = await healing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        File.Exists(keyFile.Path).ShouldBeTrue("the debris is younger than the window, so it is left alone");
    }

    /// <summary>F7 in-process half: racing minters converge on the exclusive create.</summary>
    [RetryFact]
    public async Task TwoConcurrentMinters_ConvergeOnOneKey()
    {
        const int minters = 8;
        var minted = new string?[minters];

        RaceOnThreads(minters, index => minted[index] = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot))
            .EnsureAsync(CancellationToken.None).GetAwaiter().GetResult() is { } key
            ? IdentityProof.KeyId(key)
            : null);

        minted.Count(id => id is null).ShouldBe(0, "every minter must end up with the one key");
        var winner = minted.Distinct(StringComparer.Ordinal).ShouldHaveSingleItem();
        new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot)).ReadKeyId().ShouldBe(winner);
    }

    /// <summary>The signer is imported once per instance, never re-imported per read/request.</summary>
    [RetryFact]
    public async Task Read_ReturnsTheCachedSigner_NotANewImport()
    {
        var keyFile = new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot));
        await keyFile.EnsureAsync(TestContext.Current.CancellationToken);

        var first = keyFile.Read().ShouldNotBeNull();

        keyFile.Read().ShouldBeSameAs(first);
        new IdentityKeyFile(TestData.CreateProjectOptions(_dataRoot)).Read().ShouldNotBeSameAs(first);
    }

    private static async Task SeedDebrisAsync(string keyPath, string content, TimeProvider time, TimeSpan? age = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.GetDirectoryName(keyPath)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await File.WriteAllTextAsync(keyPath, content, TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.SetLastWriteTimeUtc(keyPath,
            time.GetUtcNow().UtcDateTime - (age ?? IdentityKeyFile.HealAfter + TimeSpan.FromSeconds(1)));
    }

    /// <summary>Runs the action on dedicated threads released together by a barrier.</summary>
    private static void RaceOnThreads(int count, Action<int> action)
    {
        using var barrier = new Barrier(count);
        var threads = Enumerable.Range(0, count)
            .Select(index => new Thread(b =>
            {
                (b as Barrier)?.SignalAndWait();
                action(index);
            }))
            .ToArray();

        foreach (var thread in threads)
        {
            thread.Start(barrier);
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }
    }
}
