using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tools;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>memory_set_ttl over a real bank: the TTL an entry carries, and what a sweep then does with it.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SetTtlToolTests : IDisposable
{
    private const string ProjectId = "acme";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly string _dataRoot = TestData.CreateTempRoot("set-ttl-tool-tests");
    private readonly SqliteMemoryStore _store;
    private readonly SweepTools _tools;

    public SetTtlToolTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(factory,
            NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), _clock, TestData.CreateEmbeddingService());
        var guard = new MemoryAccessGuard(_store);
        _tools = new SweepTools(
            new SweepService(_store, _clock),
            new ForgettingPolicyService(_store, guard),
            new ToolGate(guard, new FakePromotionQueue(), new NeverMigratingStore()));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task SetTtl_UnknownHash_Throws()
    {
        await FullAccessAsync();

        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.SetTtl(ProjectId, "deadbeef", 7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetTtl_ThenSweep_DeletesTheEntry()
    {
        await FullAccessAsync();
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "forgettable note"),
            TestContext.Current.CancellationToken);

        var ttl = await _tools.SetTtl(ProjectId, entry.Hash, 7, TestContext.Current.CancellationToken);
        ttl.Data!.TtlDays.ShouldBe(7);
        ttl.Data.CanEverExpire.ShouldBeFalse(
            "a TTL is not sufficient: rating 0.5 is not below the 0.3 default threshold, so no age expires this entry");

        _clock.Advance(TimeSpan.FromDays(30));
        (await _tools.Sweep(ProjectId, dryRun: false, TestContext.Current.CancellationToken))
            .Data!.Deleted.ShouldBeEmpty("past its TTL, but the rating gate still holds it");

        await _store.SetSettingAsync(SweepThreshold.SettingKey, "0.9", TestContext.Current.CancellationToken);
        (await _tools.SetTtl(ProjectId, entry.Hash, 7, TestContext.Current.CancellationToken))
            .Data!.CanEverExpire.ShouldBeTrue("both gates are now met");

        var sweep = await _tools.Sweep(ProjectId, dryRun: false, TestContext.Current.CancellationToken);

        sweep.Data!.Deleted.ShouldBe([entry.Hash]);
    }

    [Fact]
    public async Task SetTtl_Null_ClearsTheTtl_AndZeroIsRejected()
    {
        await FullAccessAsync();
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "keep me"),
            TestContext.Current.CancellationToken);
        await _tools.SetTtl(ProjectId, entry.Hash, 7, TestContext.Current.CancellationToken);

        var cleared = await _tools.SetTtl(ProjectId, entry.Hash, null, TestContext.Current.CancellationToken);

        cleared.Data!.TtlDays.ShouldBeNull();
        cleared.Data.CanEverExpire.ShouldBeFalse("no TTL means never expires");
        await Should.ThrowAsync<ValidationException>(() =>
            _tools.SetTtl(ProjectId, entry.Hash, 0, TestContext.Current.CancellationToken));
    }

    private Task FullAccessAsync() =>
        _store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(ProjectId), "full",
            TestContext.Current.CancellationToken);
}
