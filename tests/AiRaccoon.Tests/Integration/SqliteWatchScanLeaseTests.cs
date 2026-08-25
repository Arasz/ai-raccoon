using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     SqliteWatchScanLease against a real bank: only one owner may scan a watch at a time, and a
///     crashed owner's lease is freed by expiry alone (docs/plans/2026-08-07-watch-scan-runaway-fix.md, WP4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(WatchIntegrationCollection.Name)]
public sealed class SqliteWatchScanLeaseTests : IDisposable
{
    private const string ProjectId = "acme";
    private const string Path = "/repo/docs";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-watch-lease");
    private readonly SqliteConnectionFactory _factory;
    private readonly WatchStore _store;
    private readonly FakeTimeProvider _time = new(Now);

    public SqliteWatchScanLeaseTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new WatchStore(_factory);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private SqliteWatchScanLease NewLease() => new(_factory, _time);

    private async Task RegisterWatchAsync() =>
        await _store.AddWatchAsync(ProjectId, Path, Now.ToUnixTimeSeconds(), 0,
            TestContext.Current.CancellationToken);

    [RetryFact]
    public async Task Owner_IsDistinctPerInstance()
    {
        await RegisterWatchAsync();

        NewLease().Owner.ShouldNotBe(NewLease().Owner);
    }

    [RetryFact]
    public async Task TryAcquireAsync_WhenUnowned_GrantsTheLease()
    {
        await RegisterWatchAsync();

        var granted = await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }

    [RetryFact]
    public async Task TryAcquireAsync_ForAnUnregisteredWatch_IsDenied()
    {
        var granted = await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [RetryFact]
    public async Task TryAcquireAsync_WhenHeldByALiveOwner_IsDenied()
    {
        await RegisterWatchAsync();
        var holder = NewLease();
        var rival = NewLease();
        (await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken)).ShouldBeTrue();

        var granted = await rival.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [RetryFact]
    public async Task TryAcquireAsync_ByTheSameOwnerAgain_IsReEntrant()
    {
        await RegisterWatchAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        var granted = await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }

    /// <summary>A crashed owner never releases; expiry is the only thing that frees the lease.</summary>
    [RetryFact]
    public async Task TryAcquireAsync_AfterTheHoldersLeaseExpired_IsGrantedToTheNextOwner()
    {
        await RegisterWatchAsync();
        var crashed = NewLease();
        await crashed.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        _time.Advance(SqliteWatchScanLease.LeaseTtl + TimeSpan.FromSeconds(1));
        var granted = await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }

    [RetryFact]
    public async Task TryAcquireAsync_BeforeTheHoldersLeaseExpires_IsStillDenied()
    {
        await RegisterWatchAsync();
        await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        _time.Advance(SqliteWatchScanLease.LeaseTtl - TimeSpan.FromSeconds(1));
        var granted = await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [RetryFact]
    public async Task TryRenewAsync_ByTheOwner_KeepsTheLeaseBeyondTheOriginalExpiry()
    {
        await RegisterWatchAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        _time.Advance(SqliteWatchScanLease.HeartbeatInterval);
        (await holder.TryRenewAsync(ProjectId, Path, TestContext.Current.CancellationToken)).ShouldBeTrue();

        // Past the ORIGINAL expiry but inside the renewed one: a rival must still be denied.
        _time.Advance(SqliteWatchScanLease.LeaseTtl - SqliteWatchScanLease.HeartbeatInterval);
        var granted = await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [RetryFact]
    public async Task TryRenewAsync_ByANonOwner_ReturnsFalse()
    {
        await RegisterWatchAsync();
        await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        var renewed = await NewLease().TryRenewAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        renewed.ShouldBeFalse();
    }

    [RetryFact]
    public async Task TryRenewAsync_AfterAnotherOwnerStoleTheExpiredLease_ReturnsFalse()
    {
        await RegisterWatchAsync();
        var slow = NewLease();
        await slow.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        _time.Advance(SqliteWatchScanLease.LeaseTtl + TimeSpan.FromSeconds(1));
        await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        var renewed = await slow.TryRenewAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        renewed.ShouldBeFalse();
    }

    /// <summary>Renew's WHERE also fails when the row is gone, so a removed watch stops its own scan.</summary>
    [RetryFact]
    public async Task TryRenewAsync_AfterTheWatchRowWasRemoved_ReturnsFalse()
    {
        await RegisterWatchAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        await _store.RemoveWatchAsync(ProjectId, Path, TestContext.Current.CancellationToken);
        var renewed = await holder.TryRenewAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        renewed.ShouldBeFalse();
    }

    [RetryFact]
    public async Task ReleaseAsync_ByTheOwner_LetsTheNextOwnerAcquireImmediately()
    {
        await RegisterWatchAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        await holder.ReleaseAsync(ProjectId, Path, TestContext.Current.CancellationToken);
        var granted = await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }

    [RetryFact]
    public async Task ReleaseAsync_ByANonOwner_LeavesTheLeaseIntact()
    {
        await RegisterWatchAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        await NewLease().ReleaseAsync(ProjectId, Path, TestContext.Current.CancellationToken);
        var granted = await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [RetryFact]
    public async Task ReleaseAsync_ForAnAlreadyRemovedWatch_DoesNotThrow()
    {
        await RegisterWatchAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);
        await _store.RemoveWatchAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(() =>
            holder.ReleaseAsync(ProjectId, Path, TestContext.Current.CancellationToken));
    }

    [RetryFact]
    public async Task Leases_OnDifferentWatches_DoNotBlockEachOther()
    {
        await RegisterWatchAsync();
        await _store.AddWatchAsync(ProjectId, "/repo/src", Now.ToUnixTimeSeconds(), 0,
            TestContext.Current.CancellationToken);
        await NewLease().TryAcquireAsync(ProjectId, Path, TestContext.Current.CancellationToken);

        var granted = await NewLease().TryAcquireAsync(ProjectId, "/repo/src", TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }
}
