using System.Diagnostics;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     F7: the in-process semaphore is not cross-process safe. Two genuinely separate `serve`
///     processes share one project state directory, each running the OS-locked mint/heal; the
///     second must adopt the first's key instead of deleting and re-minting it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class IdentityKeyCrossProcessTests
{
    private static readonly TimeSpan ChildStartBudget = TimeSpan.FromSeconds(120);

    [RetryFact]
    public async Task CrossProcess_MintAndHeal_ConvergeOnOneKey()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        var root = TestData.CreateTempRoot("ai-raccoon-identity-cross-process");
        try
        {
            var stateDirectory = Path.Combine(root, ".ai-raccoon");
            Directory.CreateDirectory(stateDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stateDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Debris old enough to heal, so the first process must take the mint path, not the read path.
            var keyPath = Path.Combine(stateDirectory, IdentityKeyFile.FileName);
            await File.WriteAllTextAsync(keyPath, "debris", TestContext.Current.CancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.SetLastWriteTimeUtc(keyPath, DateTime.UtcNow - TimeSpan.FromMinutes(1));
            var lockPath = IdentityKeyFile.LockPathFor(stateDirectory);

            await using var first = ChildServe.Start(root);
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                // The child's token phase precedes the identity lock; the token proves it is alive
                // and one step away from the identity critical section.
                await first.WaitForFileAsync(Path.Combine(stateDirectory, McpTokenFile.FileName),
                    ChildStartBudget, TestContext.Current.CancellationToken);
                // Hold longer than the heal window: a lock-less child deletes the debris and mints a
                // fresh key inside this window (F7), which the read-back below catches.
                await Task.Delay(IdentityKeyFile.HealAfter + TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

                first.HasUrl.ShouldBeFalse("the child must block on the identity lock, not bypass it");
                (await File.ReadAllTextAsync(keyPath, TestContext.Current.CancellationToken)).ShouldBe("debris",
                    "the child must not heal debris a live lock protects");
            }

            await first.WaitForUrlAsync(ChildStartBudget, TestContext.Current.CancellationToken);
            var firstKey = await File.ReadAllBytesAsync(keyPath, TestContext.Current.CancellationToken);
            firstKey.ShouldNotBeEmpty();
            new IdentityKeyFile(TestData.CreateProjectOptions(root)).ReadKeyId().ShouldNotBeNull();

            await using var second = ChildServe.Start(root);
            await second.WaitForUrlAsync(ChildStartBudget, TestContext.Current.CancellationToken);

            (await File.ReadAllBytesAsync(keyPath, TestContext.Current.CancellationToken)).ShouldBe(firstKey,
                "the second process must adopt the minted key, not delete and re-mint it");
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    /// <summary>A `serve` child that keeps running: its URL line is the signal that both ensures completed.</summary>
    private sealed class ChildServe : IAsyncDisposable
    {
        private readonly List<string> _lines = [];
        private readonly Process _process;
        private readonly Lock _gate = new();
        private readonly System.Text.StringBuilder _stderr = new();

        private ChildServe(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _lines.Add(e.Data);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _stderr.AppendLine(e.Data);
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public bool HasUrl
        {
            get
            {
                lock (_gate)
                {
                    return _lines.Any(Url);
                }
            }
        }

        public string Stderr
        {
            get
            {
                lock (_gate)
                {
                    return _stderr.ToString();
                }
            }
        }

        public static ChildServe Start(string dataRoot)
        {
            var startInfo = new ProcessStartInfo(RaccoonProcess.Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
                     {
                         "--data-root", dataRoot, "--install-scope", "project", "serve", "--port", "0"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            return new ChildServe(Process.Start(startInfo)!);
        }

        public async Task WaitForUrlAsync(TimeSpan budget, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + budget;
            while (DateTime.UtcNow < deadline)
            {
                if (HasUrl)
                {
                    return;
                }

                EnsureAlive();
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            throw new TimeoutException($"serve printed no URL within {budget}; stderr: {Stderr}");
        }

        public async Task WaitForFileAsync(string path, TimeSpan budget, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + budget;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    return;
                }

                EnsureAlive();
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            throw new TimeoutException($"'{path}' never appeared within {budget}; stderr: {Stderr}");
        }

        private void EnsureAlive()
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"serve exited {_process.ExitCode}; stderr: {Stderr}");
            }
        }

        private static bool Url(string line) => line.StartsWith("http://", StringComparison.Ordinal);

        public async ValueTask DisposeAsync()
        {
            RaccoonProcess.KillTree(_process);
            try
            {
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
            catch (OperationCanceledException)
            {
                // Best-effort stop; the tree is already killed.
            }

            _process.Dispose();
        }
    }
}
