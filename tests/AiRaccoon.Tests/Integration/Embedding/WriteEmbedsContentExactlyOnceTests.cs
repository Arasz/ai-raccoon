using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The duplicate-embed guarantee that <c>Embeddings_ConfigureOpenAi_RoutesThroughTheConfiguredEndpoint</c>
///     (McpServerE2ETests) used to pin down only by accident, via a request-count assertion that a
///     background maintenance sweep could legitimately race. Built directly on
///     <see cref="SqliteMemoryStore" /> with a counting <see cref="CountingEmbeddingService" /> and no
///     hosted service anywhere in the process — nothing can sweep pending rows here, so a failure can
///     only mean the write path itself embedded the content more than once.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WriteEmbedsContentExactlyOnceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private SqliteConnectionFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Write_WithAnEngineConfigured_EmbedsTheContentExactlyOnce()
    {
        var embeddings = new CountingEmbeddingService();
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            embeddings);
        await store.ConfigureEmbeddingAsync("local", null, null, TestContext.Current.CancellationToken);

        await store.WriteAsync(new MemoryWriteRequest("acme", "single embed guarantee fact"),
            TestContext.Current.CancellationToken);

        // Exactly one call carrying exactly this content: zero would mean the write left it
        // pending, two would mean the write path embedded it twice.
        embeddings.CallCountFor("single embed guarantee fact").ShouldBe(1);
        embeddings.Calls.Count.ShouldBe(1, "no other generator call may have happened for this write");
    }
}
