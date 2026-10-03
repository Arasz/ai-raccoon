using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The store's parameterless reads pass their token through <see cref="Sql.Def(string, CancellationToken, SqliteTransaction?)" />;
///     if a class-local <c>Def(sql, object? = null, CancellationToken = default)</c> shadowed it the token
///     would be bound as Dapper parameters and the read would ignore cancellation (#826). The token is
///     cancelled after the connection opens, because opening already rejects a token cancelled up front.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ReadCancellationTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-read-cancellation");
    private readonly SqliteConnectionFactory _realFactory;

    public ReadCancellationTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _realFactory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task GetProjectIdsAsync_CancelledAfterTheBankOpens_IsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var store = StoreCancellingAfterOpen(cts);

        await Should.ThrowAsync<OperationCanceledException>(() => store.GetProjectIdsAsync(cts.Token));
    }

    [RetryFact]
    public async Task GetSharedIndexAsync_CancelledAfterTheBankOpens_IsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var store = StoreCancellingAfterOpen(cts);

        await Should.ThrowAsync<OperationCanceledException>(() => store.GetSharedIndexAsync(cts.Token));
    }

    private SqliteMemoryStore StoreCancellingAfterOpen(CancellationTokenSource cts)
    {
        var factory = Substitute.For<ISqliteConnectionFactory>();
        factory.OpenBankAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            var connection = await _realFactory.OpenBankAsync(CancellationToken.None);
            await cts.CancelAsync();
            return connection;
        });

        return TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_realFactory), TestData.RealMarkdownChunker(), TimeProvider.System,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }
}
