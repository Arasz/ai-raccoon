using System.Globalization;
using System.Runtime.Versioning;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     F49 end to end (ADR-0106 D7): a project-scope root driven by the real binaries — quiet
///     `serve` bootstrapping a fresh root, a proxy session, a settings verb — keeps every secret in
///     the bank state directory. The root's top level holds nothing but that directory, so no 0600
///     secret sits one `git add -A` from a commit; and a backup of the state directory alone carries
///     the token and the identity key, and restores into a root the same credentials serve again.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
[UnsupportedOSPlatform("windows")]
public sealed class SecretPlacementE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(120);

    private readonly string _backup = TestData.CreateTempRoot("secret-placement-backup");
    private readonly string _root = TestData.CreateTempRoot("secret-placement");
    private IAsyncDisposable? _env;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _env = await EnvScope.AcquireAsync(Ct, (EnvEncryptionKeyProvider.EnvVarName, null));

    public async ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_root);
        TestData.DeleteTempRoot(_backup);
        if (_env is not null)
        {
            await _env.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProjectScopeRun_LeavesNoSecretAtTheDataRootTopLevel()
    {
        var options = TestData.CreateProjectOptions(_root);
        var stateDirectory = BankPaths.DirectoryFor(options);
        var port = FreePort();

        await using (await StartQuietServeAsync(options, port))
        {
            await using var proxy = ProxyProcess.Start(
            [
                "--data-root", _root, "--install-scope", "project", "--quiet",
                "--port", port.ToString(CultureInfo.InvariantCulture)
            ], ProxyProcess.Stateless);
            (await proxy.ListToolsAsync(Ct)).ShouldNotBeEmpty();
            (await proxy.CloseAsync(HardCap)).ShouldBe(ExitCode.Success, proxy.Stderr);

            var show = await RaccoonProcess.RunAsync(
            [
                "--data-root", _root, "--install-scope", "project", "--quiet",
                "--port", port.ToString(CultureInfo.InvariantCulture), "settings", "sweep", "show"
            ], HardCap, Ct);
            show.ExitCode.ShouldBe(ExitCode.Success, show.Stderr);
        }

        // The top level holds the state directory and nothing else: no token, no key, no lock, no log, no bank.
        Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).ShouldBe([".ai-raccoon"]);
        var token = File.ReadAllText(Path.Combine(stateDirectory, McpTokenFile.FileName));
        var keyId = new IdentityKeyFile(options).ReadKeyId().ShouldNotBeNull();
        foreach (var secret in new[] { McpTokenFile.FileName, IdentityKeyFile.FileName })
        {
            File.GetUnixFileMode(Path.Combine(stateDirectory, secret))
                .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, secret);
        }

        // A backup of the state directory alone carries both secrets, and a restore serves them unchanged.
        var backup = Path.Combine(_backup, ".ai-raccoon");
        CopyDirectory(stateDirectory, backup);
        File.ReadAllText(Path.Combine(backup, McpTokenFile.FileName)).ShouldBe(token);
        File.Exists(Path.Combine(backup, IdentityKeyFile.FileName)).ShouldBeTrue();

        Directory.Delete(stateDirectory, recursive: true);
        CopyDirectory(backup, stateDirectory);
        await using (await StartQuietServeAsync(options, port))
        {
            await using var proxy = ProxyProcess.Start(
            [
                "--data-root", _root, "--install-scope", "project", "--quiet",
                "--port", port.ToString(CultureInfo.InvariantCulture)
            ], ProxyProcess.Stateless);
            (await proxy.ListToolsAsync(Ct)).ShouldNotBeEmpty();
            (await proxy.CloseAsync(HardCap)).ShouldBe(ExitCode.Success, proxy.Stderr);
            proxy.Stderr.ShouldNotContain("did not prove", Case.Sensitive, "the restored key must prove to the restored root");
        }

        File.ReadAllText(Path.Combine(stateDirectory, McpTokenFile.FileName)).ShouldBe(token, "the restore must not re-mint the token");
        new IdentityKeyFile(options).ReadKeyId().ShouldBe(keyId, "the restore must not re-mint the identity key");
        Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).ShouldBe([".ai-raccoon"]);
    }

    private static Task<RealServe> StartQuietServeAsync(InfrastructureOptions options, int port) =>
        RealServe.StartAsync(options, ["--quiet", "serve", "--port", port.ToString(CultureInfo.InvariantCulture)], port, Ct);

    /// <summary>A backup as an operator takes one: every file, with its mode, into an owner-only directory.</summary>
    private static void CopyDirectory(string source, string target)
    {
        BankPaths.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var copy = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, copy);
            File.SetUnixFileMode(copy, File.GetUnixFileMode(file));
        }
    }

    private static int FreePort()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        return port;
    }
}
