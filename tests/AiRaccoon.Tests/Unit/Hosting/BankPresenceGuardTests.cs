using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     F39: the pure decision the two client auto-launch paths (<c>BackendSessions.AcquireBackend</c>,
///     <c>CliSettingsBackend.AcquireAsync</c>) share before any probe or spawn — a mistyped
///     <c>--data-root</c> must never mint a bank. Exercised directly against the guard so the
///     default-root control never has to touch the real <c>~/.ai-raccoon</c> on disk: that branch
///     returns before any <see cref="File.Exists(string)" /> call at all.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankPresenceGuardTests
{
    [Fact]
    public void EnsureExists_AgainstTheDefaultRoot_NeverThrows_WithoutCheckingTheFilesystem()
    {
        var options = new InfrastructureOptions { DataRoot = DefaultOptions.DataRoot, Scope = InstallScope.User };

        Should.NotThrow(() => BankPresenceGuard.EnsureExists(options));
    }

    /// <summary>
    ///     Path identity, not "was --data-root passed": a spelling of the default root that is not
    ///     byte-identical (a trailing separator, or a "go up and back down" detour) must still be
    ///     recognised as the same root once expanded.
    /// </summary>
    [Fact]
    public void EnsureExists_AgainstADifferentSpellingOfTheDefaultRoot_StillExempts()
    {
        var detour = Path.Combine(DefaultOptions.DataRoot, "..", Path.GetFileName(DefaultOptions.DataRoot) + Path.DirectorySeparatorChar);
        var options = new InfrastructureOptions { DataRoot = detour, Scope = InstallScope.User };

        Should.NotThrow(() => BankPresenceGuard.EnsureExists(options));
    }

    [Fact]
    public void EnsureExists_AgainstAnEmptyNonDefaultRoot_ThrowsBankMissing_NamingThePathAndTheRemedy()
    {
        var dataRoot = TestData.CreateTempRoot("bank-presence-guard-empty");
        try
        {
            var options = TestData.CreateInfrastructureOptions(dataRoot);

            var error = Should.Throw<BankMissingException>(() => BankPresenceGuard.EnsureExists(options));

            error.Message.ShouldContain(dataRoot);
            error.Message.ShouldContain("ai-raccoon serve --data-root");
            new DirectoryInfo(dataRoot).EnumerateFileSystemInfos().ShouldBeEmpty(
                "the guard must refuse before minting anything — it only ever reads");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>The positive control: an already-bootstrapped non-default root passes straight through.</summary>
    [Fact]
    public async Task EnsureExists_AgainstANonDefaultRootHoldingARealBank_DoesNotThrow()
    {
        var dataRoot = TestData.CreateTempRoot("bank-presence-guard-seeded");
        try
        {
            var options = TestData.CreateInfrastructureOptions(dataRoot);
            await TestData.SeedBankAsync(options, TestContext.Current.CancellationToken);

            Should.NotThrow(() => BankPresenceGuard.EnsureExists(options));
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }
}
