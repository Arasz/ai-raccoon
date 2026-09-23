using System.Buffers.Text;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     The F49 upgrade on the built binary (ADR-0106 D3): a project root left by an earlier version
///     keeps its token at the data-root top level. The first `serve` adopts that token into the
///     state directory, deletes the legacy file only once the adoption is written, mints the
///     identity key beside it, and the proxy then proves the server and attaches with the token it
///     finds in the new place — the same token clients already held.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Trait(TestCategories.Retry, TestCategories.Never)]
[Collection(E2ETestCollection.Name)]
[UnsupportedOSPlatform("windows")]
public sealed class TokenPathMigrationE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(120);

    private readonly string _root = TestData.CreateTempRoot("token-path-migration");
    private IAsyncDisposable? _env;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _env = await EnvScope.AcquireAsync(Ct, (EnvEncryptionKeyProvider.EnvVarName, null));

    public async ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_root);
        if (_env is not null)
        {
            await _env.DisposeAsync();
        }
    }

    [Fact]
    public async Task Serve_WithALegacyTokenFile_MigratesIt_AndTheProxyStillAttaches()
    {
        var options = TestData.CreateProjectOptions(_root);
        var stateDirectory = BankPaths.DirectoryFor(options);
        await TestData.SeedBankAsync(options, Ct);
        var legacyPath = Path.Combine(_root, McpTokenFile.FileName);
        var legacyToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        await File.WriteAllTextAsync(legacyPath, legacyToken, Ct);
        File.SetUnixFileMode(legacyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Exists(Path.Combine(stateDirectory, McpTokenFile.FileName)).ShouldBeFalse();
        File.Exists(Path.Combine(stateDirectory, IdentityKeyFile.FileName)).ShouldBeFalse();
        var port = FreePort();

        await using (await RealServe.StartAsync(options, port, Ct))
        {
            File.Exists(legacyPath).ShouldBeFalse("the adopted legacy token must not be left at the top level");
            (await File.ReadAllTextAsync(Path.Combine(stateDirectory, McpTokenFile.FileName), Ct)).Trim()
                .ShouldBe(legacyToken, "the token clients already held is adopted, not replaced");
            new IdentityKeyFile(options).ReadKeyId().ShouldNotBeNull("serve mints the identity key beside the adopted token");

            await using var proxy = ProxyProcess.Start(
            [
                "--data-root", _root, "--install-scope", "project",
                "--port", port.ToString(CultureInfo.InvariantCulture)
            ], ProxyProcess.Stateless);
            (await proxy.ListToolsAsync(Ct)).ShouldNotBeEmpty();
            (await proxy.CloseAsync(HardCap)).ShouldBe(ExitCode.Success, proxy.Stderr);
            proxy.Stderr.ShouldNotContain("did not prove", Case.Sensitive, "the migrated root must prove and attach, not fall back");
        }

        Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName).ShouldBe([".ai-raccoon"]);
    }

    private static int FreePort()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        return port;
    }
}
