using AiRaccoon.Core.Memory;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PermissiveFakeMemoryStoreTests
{
    [Fact]
    public async Task ReadMembers_ReturnEmptyOrNotFound()
    {
        IMemoryStore store = new PermissiveFakeMemoryStore();
        var ct = TestContext.Current.CancellationToken;

        (await store.SearchAsync(new SearchQuery("acme", "q"), ct)).Results.ShouldBeEmpty();
        (await store.GetAsync("acme", "h", ct)).ShouldBeNull();
        (await store.GetMetadataAsync("acme", "h", ct)).ShouldBeNull();
        (await store.ListContextAsync("acme", "project:acme", ct)).ShouldBeEmpty();
        (await store.GetProjectIdsAsync(ct)).ShouldBeEmpty();
        (await store.GetSettingAsync("any.key", ct)).ShouldBeNull();
        (await store.GetSettingsByPrefixAsync("any.", ct)).ShouldBeEmpty();
        (await store.GetStatsAsync("acme", ct)).EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task WriteMembers_ReportSuccessAndKeepNothing()
    {
        IMemoryStore store = new PermissiveFakeMemoryStore();
        var ct = TestContext.Current.CancellationToken;

        await store.SetSettingAsync("k", "v", ct);
        await store.DeleteSettingAsync("k", ct);

        (await store.GetSettingAsync("k", ct)).ShouldBeNull();
        (await store.DeleteAsync("acme", "h", ct)).ShouldBe(1);
        (await store.SetEntryTtlAsync("acme", "h", 7, ct)).ShouldBeTrue();
        (await store.IngestFileAsync("acme", "a.md", null, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task OverrideTakesPrecedenceOverTheDefault()
    {
        IMemoryStore store = new SettingStore();

        (await store.GetSettingAsync("k", TestContext.Current.CancellationToken)).ShouldBe("seeded");
    }

    /// <summary>No default is benign for a migration, so it keeps the throwing base behaviour.</summary>
    [Fact]
    public async Task StartModelMigration_StillThrowsNamingItself()
    {
        IMemoryStore store = new PermissiveFakeMemoryStore();

        var thrown = await Should.ThrowAsync<NotSupportedException>(() =>
            store.StartModelMigrationAsync("local", null, null, TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(nameof(IMemoryStore.StartModelMigrationAsync));
    }

    private sealed class SettingStore : PermissiveFakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("seeded");
    }
}
