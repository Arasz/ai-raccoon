using AiRaccoon.Access;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests;
using AiRaccoon.Tools;
using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>set_memory_ttl over a real bank: the TTL an entry carries, and what a sweep then does with it.</summary>
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
            NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), _clock, new CountingEmbeddingService(), null, null, null, null, null, null, null);
        var guard = new MemoryAccessGuard(_store);
        _tools = new SweepTools(
            new SweepService(_store, _clock),
            new ForgettingPolicyService(_store, guard),
            new ToolGate(guard, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), TestData.ScopeCheck));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task SetTtl_UnknownHash_Throws()
    {
        await FullAccessAsync();

        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.SetTtl(ProjectId, "deadbeef", 7, TestContext.Current.CancellationToken));
    }

    [RetryFact]
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

    [RetryFact]
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

    [RetryTheory]
    [InlineData("file")]
    [InlineData("none")]
    public async Task SettingTtl_OnNonMemoryRow_IsRefusedWithoutChangingHistoricalTtl(string entryType)
    {
        await FullAccessAsync();
        var entry = await SeedHistoricalTtlAsync("historical file", entryType, ttlDays: 19);

        var refusal = await Should.ThrowAsync<NotAMemoryEntryException>(() =>
            _tools.SetTtl(ProjectId, entry.Hash, 7, TestContext.Current.CancellationToken));

        ToolRefusals.PrefixFor(refusal).ShouldBe("not-a-memory-entry");

        (await _store.GetMetadataAsync(ProjectId, entry.Hash, TestContext.Current.CancellationToken))!
            .TtlDays.ShouldBe(19);
    }

    [RetryTheory]
    [InlineData("file")]
    [InlineData("none")]
    public async Task ClearingTtl_OnNonMemoryRow_ClearsTheHistoricalTtl(string entryType)
    {
        await FullAccessAsync();
        var entry = await SeedHistoricalTtlAsync("historical file", entryType, ttlDays: 19);

        var cleared = await _tools.SetTtl(ProjectId, entry.Hash, null, TestContext.Current.CancellationToken);

        cleared.Data!.TtlDays.ShouldBeNull();
        (await _store.GetMetadataAsync(ProjectId, entry.Hash, TestContext.Current.CancellationToken))!
            .TtlDays.ShouldBeNull("a TTL stored before 2.1.0 is still honoured by the sweep, so it must stay clearable");
    }

    [RetryFact]
    public async Task Sweep_AfterClearingAHistoricalFileTtl_KeepsOnlyTheClearedRow()
    {
        await FullAccessAsync();
        var expiring = await SeedHistoricalTtlAsync("file left to expire", "file", ttlDays: 1, rating: 0.1);
        var cleared = await SeedHistoricalTtlAsync("file whose ttl is cleared", "file", ttlDays: 1, rating: 0.1);

        await _tools.SetTtl(ProjectId, cleared.Hash, null, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(2));
        var result = await _tools.Sweep(ProjectId, dryRun: false, TestContext.Current.CancellationToken);

        result.Data!.Deleted.ShouldBe([expiring.Hash]);
        (await _store.GetMetadataAsync(ProjectId, cleared.Hash, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [RetryFact]
    public async Task ChunkedTranscript_CanSetAndClearTtl()
    {
        await FullAccessAsync();
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "session account",
            SourceFile: "hermes/session-1"), TestContext.Current.CancellationToken);
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET total_chunks = 5 WHERE hash = @hash", new { hash = entry.Hash },
            cancellationToken: TestContext.Current.CancellationToken));

        (await _tools.SetTtl(ProjectId, entry.Hash, 7, TestContext.Current.CancellationToken)).Data!.TtlDays.ShouldBe(7);
        (await _tools.SetTtl(ProjectId, entry.Hash, null, TestContext.Current.CancellationToken)).Data!.TtlDays.ShouldBeNull();
        (await _store.GetMetadataAsync(ProjectId, entry.Hash, TestContext.Current.CancellationToken))!.TtlDays.ShouldBeNull();
    }

    [RetryFact]
    public async Task WrongProjectHash_HasTheSameRefusalAsMissingHash()
    {
        await FullAccessAsync();
        var entry = await _store.WriteAsync(new MemoryWriteRequest("other", "private memory"),
            TestContext.Current.CancellationToken);
        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.SetTtl(ProjectId, entry.Hash, 7, TestContext.Current.CancellationToken));
        (await _store.GetMetadataAsync("other", entry.Hash, TestContext.Current.CancellationToken))!.TtlDays.ShouldBeNull();
    }

    [RetryTheory]
    [InlineData(7)]
    [InlineData(null)]
    public async Task WrongProjectFileHash_IsUnknownHash_NotAKindRefusal(int? ttlDays)
    {
        await FullAccessAsync();
        var entry = await _store.WriteAsync(new MemoryWriteRequest("other", "another project's file"),
            TestContext.Current.CancellationToken);
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET entry_type = 'file', ttl_days = 19 WHERE hash = @hash",
            new { hash = entry.Hash }, cancellationToken: TestContext.Current.CancellationToken));

        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.SetTtl(ProjectId, entry.Hash, ttlDays, TestContext.Current.CancellationToken));

        (await _store.GetMetadataAsync("other", entry.Hash, TestContext.Current.CancellationToken))!.TtlDays.ShouldBe(19);
    }

    [RetryTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(36501)]
    public async Task OutOfRangeTtl_DoesNotChangeStoredTtl(int ttlDays)
    {
        await FullAccessAsync();
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "bounded expiry"),
            TestContext.Current.CancellationToken);
        await _tools.SetTtl(ProjectId, entry.Hash, 9, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ValidationException>(() =>
            _tools.SetTtl(ProjectId, entry.Hash, ttlDays, TestContext.Current.CancellationToken));

        (await _store.GetMetadataAsync(ProjectId, entry.Hash, TestContext.Current.CancellationToken))!.TtlDays.ShouldBe(9);
    }

    [RetryFact]
    public async Task Sweep_HonorsHistoricalExplicitFileTtl()
    {
        await FullAccessAsync();
        var historical = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "historically expiring file"),
            TestContext.Current.CancellationToken);
        var permanent = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "file without expiry"),
            TestContext.Current.CancellationToken);
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET entry_type = 'file', rating = 0.1, ttl_days = CASE WHEN hash = @hash THEN 7 END",
            new { hash = historical.Hash }, cancellationToken: TestContext.Current.CancellationToken));
        _clock.Advance(TimeSpan.FromDays(8));

        var result = await _tools.Sweep(ProjectId, dryRun: false, TestContext.Current.CancellationToken);

        result.Data!.Deleted.ShouldBe([historical.Hash]);
        (await _store.GetMetadataAsync(ProjectId, historical.Hash, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await _store.GetMetadataAsync(ProjectId, permanent.Hash, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    private async Task<MemoryEntry> SeedHistoricalTtlAsync(string value, string entryType, int ttlDays,
        double? rating = null)
    {
        var entry = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, value),
            TestContext.Current.CancellationToken);
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET entry_type = @entryType, ttl_days = @ttlDays, rating = COALESCE(@rating, rating) WHERE hash = @hash",
            new { hash = entry.Hash, entryType, ttlDays, rating }, cancellationToken: TestContext.Current.CancellationToken));
        return entry;
    }

    private Task FullAccessAsync() =>
        _store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(ProjectId), "full",
            TestContext.Current.CancellationToken);
}
