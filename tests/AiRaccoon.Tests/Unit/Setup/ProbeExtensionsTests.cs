using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Setup.Extensions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     The encryption probes turn a failure into a result, but a Ctrl-C is not a failed key: it
///     must propagate so the command exits Interrupted instead of "wrong key".
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProbeExtensionsTests
{
    [Fact]
    public async Task ResolvingTheKey_WhenTheCallerCancels_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var resolver = Substitute.For<IEncryptionKeyResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns<ResolvedKey>(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => resolver.ProbeResolvingEncryptionKeyAsync(cts.Token));
    }

    [Fact]
    public async Task ResolvingTheKey_WhenTheSourceFails_ReportsAFailure()
    {
        var resolver = Substitute.For<IEncryptionKeyResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns<ResolvedKey>(_ => throw new InvalidOperationException("no bws"));

        var result = await resolver.ProbeResolvingEncryptionKeyAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task OpeningWithTheKey_WhenTheCallerCancels_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var factory = Substitute.For<ISqliteConnectionFactory>();
        factory.OpenBankWithKeyAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Microsoft.Data.Sqlite.SqliteConnection>(_ => throw new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => factory.ProbeUsingEncryptionKey("key", cts.Token));
    }
}
