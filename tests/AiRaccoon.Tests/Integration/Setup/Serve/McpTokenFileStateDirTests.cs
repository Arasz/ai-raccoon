using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     F49: the loopback token lives in the bank state directory — the data root for user scope,
///     &lt;dataRoot&gt;/.ai-raccoon for project scope — never at a project root's top level. The
///     existing directory and token are owner-only or the mint refuses (D1).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class McpTokenFileStateDirTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-token-state-dir");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task Token_ResolvesInsideTheBankStateDirectory_NotTheDataRootTopLevel()
    {
        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrWhiteSpace();
        tokenFile.Path.ShouldBe(Path.Combine(_dataRoot, ".ai-raccoon", McpTokenFile.FileName));
        File.Exists(Path.Combine(_dataRoot, McpTokenFile.FileName)).ShouldBeFalse();
        (await File.ReadAllTextAsync(tokenFile.Path, TestContext.Current.CancellationToken)).ShouldBe(token);
    }

    /// <summary>Positive control: user scope is the pre-F49 layout, unchanged by design.</summary>
    [RetryFact]
    public async Task UserScope_Token_StaysAtTheDataRootTopLevel()
    {
        var tokenFile = new McpTokenFile(TestData.CreateInfrastructureOptions(_dataRoot));

        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrWhiteSpace();
        tokenFile.Path.ShouldBe(Path.Combine(_dataRoot, McpTokenFile.FileName));
    }

    [RetryFact]
    public async Task Ensure_CreatesTheStateDirectory_0700_AndTheToken_0600()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("UnixFileMode is POSIX-only; on Windows the file inherits the data-root ACL");
            return;
        }

        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        File.GetUnixFileMode(tokenFile.StateDirectory)
            .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(tokenFile.Path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    ///     F2/D1: a state directory another principal can read or write is a planted trust anchor.
    ///     The mint refuses rather than writing a secret into it, and names the remedy.
    /// </summary>
    [RetryFact]
    public async Task Ensure_OnAPlantedPermissiveStateDirectory_FailsClosed_NamingTheRemedy()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX modes only; Windows ACL inheritance is the documented residual");
            return;
        }

        var stateDirectory = Path.Combine(_dataRoot, ".ai-raccoon");
        Directory.CreateDirectory(stateDirectory);
        File.SetUnixFileMode(stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        tokenFile.RefusalReason.ShouldNotBeNull().ShouldContain(stateDirectory);
        tokenFile.RefusalReason.ShouldContain("chmod 700");
        Directory.GetFileSystemEntries(stateDirectory).ShouldBeEmpty();
    }

    /// <summary>The client side of the same refusal: never read a token out of a shared directory.</summary>
    [RetryFact]
    public async Task Read_OnAPlantedPermissiveStateDirectory_FailsClosed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX modes only; Windows ACL inheritance is the documented residual");
            return;
        }

        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));
        await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);
        File.SetUnixFileMode(tokenFile.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                                        UnixFileMode.UserExecute | UnixFileMode.OtherRead |
                                                        UnixFileMode.OtherExecute);

        var read = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot)).Read();

        read.ShouldBeNull();
    }
}
