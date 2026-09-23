using AiRaccoon.Hosting.Common;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     F49/A3 upgrade window: a token an older build wrote at the project root's top level is read
///     in preference to nothing, adopted into the state directory once, and only then deleted. It is
///     validated first (F6) — a planted or unparseable file fails closed and is never adopted, and
///     the state directory is never re-minted over it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class TokenFileLegacyMigrationTests : IDisposable
{
    /// <summary>A well-formed token of the exact length the mint produces (43 base64url chars).</summary>
    private static readonly string LegacyToken = new('A', 43);

    private static readonly string OtherToken = new('B', 43);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-token-legacy");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private string LegacyPath => Path.Combine(_dataRoot, McpTokenFile.FileName);

    private string StatePath => Path.Combine(_dataRoot, ".ai-raccoon", McpTokenFile.FileName);

    [RetryFact]
    public async Task LegacyToken_IsMigrated_Validated_DeletedAfterSuccess_AndNeverReMinted()
    {
        await SeedOwnerOnlyFileAsync(LegacyPath, LegacyToken);
        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        var migrated = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        migrated.ShouldBe(LegacyToken);
        (await File.ReadAllTextAsync(tokenFile.Path, TestContext.Current.CancellationToken)).ShouldBe(LegacyToken);
        File.Exists(LegacyPath).ShouldBeFalse("the legacy file is deleted only after the state-dir write succeeded");

        // A second ensure adopts the state-dir token and never re-mints.
        (await tokenFile.EnsureAsync(TestContext.Current.CancellationToken)).ShouldBe(LegacyToken);
        File.Exists(LegacyPath).ShouldBeFalse();
    }

    /// <summary>F6: a legacy file another principal can read or write was planted, not inherited.</summary>
    [RetryFact]
    public async Task LegacyToken_ThatIsPermissive_IsRefused_NotAdopted_AndNotDeleted()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("POSIX modes only; Windows ACL inheritance is the documented residual");
            return;
        }

        await SeedOwnerOnlyFileAsync(LegacyPath, LegacyToken);
        File.SetUnixFileMode(LegacyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        tokenFile.RefusalReason.ShouldNotBeNull().ShouldContain(LegacyPath);
        tokenFile.RefusalReason.ShouldContain("chmod 600");
        File.Exists(LegacyPath).ShouldBeTrue();
        File.Exists(StatePath).ShouldBeFalse("a refused legacy token must not be adopted");
    }

    [RetryFact]
    public async Task LegacyToken_ThatIsUnparseable_IsRefused_NotMigrated_AndNotDeleted()
    {
        await SeedOwnerOnlyFileAsync(LegacyPath, "truncated");
        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        var token = await tokenFile.EnsureAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        tokenFile.RefusalReason.ShouldNotBeNull().ShouldContain(LegacyPath);
        (await File.ReadAllTextAsync(LegacyPath, TestContext.Current.CancellationToken)).ShouldBe("truncated");
        File.Exists(StatePath).ShouldBeFalse();
    }

    [RetryFact]
    public async Task WhenBothPathsHoldAToken_TheStateDirectoryWins_AndTheLegacyFileIsNotDeleted()
    {
        await SeedOwnerOnlyFileAsync(LegacyPath, LegacyToken);
        await SeedOwnerOnlyFileAsync(StatePath, OtherToken);
        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        tokenFile.Read().ShouldBe(OtherToken);
        (await tokenFile.EnsureAsync(TestContext.Current.CancellationToken)).ShouldBe(OtherToken);

        (await File.ReadAllTextAsync(StatePath, TestContext.Current.CancellationToken)).ShouldBe(OtherToken);
        File.Exists(LegacyPath).ShouldBeTrue("nothing was written this run, so nothing authorises a delete");
    }

    /// <summary>Positive control: readers see the legacy token without writing anything.</summary>
    [RetryFact]
    public async Task Read_FallsBackToAValidLegacyToken_WithoutMigrating()
    {
        await SeedOwnerOnlyFileAsync(LegacyPath, LegacyToken);
        var tokenFile = new McpTokenFile(TestData.CreateProjectOptions(_dataRoot));

        tokenFile.Read().ShouldBe(LegacyToken);

        File.Exists(StatePath).ShouldBeFalse();
        File.Exists(LegacyPath).ShouldBeTrue();
    }

    private static async Task SeedOwnerOnlyFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.GetDirectoryName(path)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
