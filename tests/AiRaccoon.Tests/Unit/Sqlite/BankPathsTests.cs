using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Sqlite;

/// <summary>
///     F49's one source of truth for the bank's state directory. Every path that used to hand-roll
///     `Path.Combine(dataRoot, …)` — the bank itself, the token, the identity key, the log — must
///     resolve through here so user and project scope can never drift apart.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankPathsTests
{
    [Fact]
    public void DirectoryFor_UserScope_IsTheDataRoot() =>
        BankPaths.DirectoryFor(new InfrastructureOptions { DataRoot = "/tmp/root", Scope = InstallScope.User })
            .ShouldBe("/tmp/root");

    [Fact]
    public void DirectoryFor_ProjectScope_IsTheHiddenStateDirectoryInsideTheDataRoot() =>
        BankPaths.DirectoryFor(new InfrastructureOptions { DataRoot = "/tmp/root", Scope = InstallScope.Project })
            .ShouldBe(Path.Combine("/tmp/root", ".ai-raccoon"));

    /// <summary>
    ///     Derive-or-delete: the factory's public path and the new accessor are one resolution, not
    ///     two copies that can drift.
    /// </summary>
    [Theory]
    [InlineData(InstallScope.User)]
    [InlineData(InstallScope.Project)]
    public void TheFactoryBankPath_DelegatesToTheSameResolution(InstallScope scope)
    {
        var options = new InfrastructureOptions { DataRoot = "/tmp/root", Scope = scope };

        SqliteConnectionFactory.BankPathFor(options)
            .ShouldBe(Path.Combine(BankPaths.DirectoryFor(options), "memory.db"));
    }

    [Fact]
    public void DirectoryFor_ForAnUnknownScope_Refuses() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            BankPaths.DirectoryFor(new InfrastructureOptions { DataRoot = "/tmp/root", Scope = (InstallScope)42 }));
}
