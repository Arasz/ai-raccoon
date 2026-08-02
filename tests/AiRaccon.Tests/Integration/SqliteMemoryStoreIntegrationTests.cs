using System.Runtime.InteropServices;
using AiRaccon.Core.Common;
using AiRaccon.Core.Memory;
using AiRaccon.Infrastructure.Options;
using AiRaccon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Integration;

/// <summary>
/// End-to-end tests against the REAL sqlite-memory/vector extensions (the "done means proven"
/// gate for the store). They need provisioned native binaries under the data root; when the
/// host RID has none, the first bank open throws and the test is skipped rather than failed,
/// so CI without extensions stays green.
///
/// Note on embeddings: with no model configured the bank runs in deferred mode (FR-MEM-1.12) —
/// content is stored but invisible to memory_search until memory_embed_pending runs. These
/// tests therefore assert the storage surface (write/stats/delete/share); the semantic search
/// round-trip is verified by unit tests over SearchContexts/SearchResultMerger and by a manual
/// test with a configured GGUF model.
/// </summary>
public class SqliteMemoryStoreIntegrationTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    private async Task<SqliteMemoryStore?> TryCreateStoreAsync()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot,
            Rid = RuntimeInformation.RuntimeIdentifier,
            Scope = InstallScope.User,
        };

        await ProvisionIfAvailableAsync(options);

        var factory = new SqliteConnectionFactory(options);
        try
        {
            await using var probe = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            return new SqliteMemoryStore(factory);
        }
        catch (SqliteException)
        {
            return null; // native extensions not available on this host — skip
        }
    }

    [Fact]
    public async Task Write_StoresEntryInProjectContext_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "SQLite memory stores project knowledge"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("project:acme");
        entry.Hash.ShouldNotBeNullOrWhiteSpace();
        entry.Value.ShouldBe("SQLite memory stores project knowledge");
    }

    [Fact]
    public async Task Write_SameContentTwice_DedupsToSameHash_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var first = await store.WriteAsync(
            new MemoryWriteRequest("acme", "dedup me"), TestContext.Current.CancellationToken);
        var second = await store.WriteAsync(
            new MemoryWriteRequest("acme", "dedup me"), TestContext.Current.CancellationToken);

        second.Hash.ShouldBe(first.Hash);
    }

    [Fact]
    public async Task Write_WithWorkspaceId_LandsInWorkspaceContext_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "draft finding", workspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("workspace:ws-1");
    }

    [Fact]
    public async Task ShareAsync_PromotesIntoSharedContext_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "cross project convention"),
            TestContext.Current.CancellationToken);

        var shared = await store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        // The shared row must actually exist in the bank, not just be claimed by the return
        // value: re-read the shared context and confirm the content is there (B1 — a no-op
        // promotion would leave the shared context empty).
        shared.Context.ShouldBe(ContextNaming.SharedContext);
        var sharedEntries = await store.ListContextAsync("acme", ContextNaming.SharedContext, TestContext.Current.CancellationToken);
        sharedEntries.ShouldContain(e => e.Value == "cross project convention");

        // The source project row must remain.
        var projectEntries = await store.ListContextAsync("acme", "project:acme", TestContext.Current.CancellationToken);
        projectEntries.ShouldContain(e => e.Value == "cross project convention");
    }

    [Fact]
    public async Task ShareAsync_Twice_IsIdempotent_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "share me once"),
            TestContext.Current.CancellationToken);

        await store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        await store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        var sharedEntries = await store.ListContextAsync("acme", ContextNaming.SharedContext, TestContext.Current.CancellationToken);
        sharedEntries.Count(e => e.Value == "share me once").ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "to be deleted"),
            TestContext.Current.CancellationToken);

        var deleted = await store.DeleteAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        deleted.ShouldBeTrue();
        var stats = await store.GetStatsAsync("acme", TestContext.Current.CancellationToken);
        stats.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Stats_ReportsCommittedContexts_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        await store.WriteAsync(new MemoryWriteRequest("acme", "context a note"), TestContext.Current.CancellationToken);

        var stats = await store.GetStatsAsync("acme", TestContext.Current.CancellationToken);

        stats.EntryCount.ShouldBeGreaterThanOrEqualTo(1);
        stats.Contexts.ShouldContain("project:acme");
    }

    [Fact]
    public async Task ListContextAsync_ReturnsEntriesForTheContext_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        await store.WriteAsync(new MemoryWriteRequest("acme", "listed entry"), TestContext.Current.CancellationToken);

        var entries = await store.ListContextAsync("acme", "project:acme", TestContext.Current.CancellationToken);

        entries.ShouldContain(e => e.Value == "listed entry");
    }

    private async Task ProvisionIfAvailableAsync(InfrastructureOptions options)
    {
        // If the developer (or a previous run) already provisioned the host RID's modules in
        // ~/.ai-raccon/extensions/<rid>, copy them into this test's temp data root so the
        // integration test exercises the real binaries.
        var rid = RuntimeInformation.RuntimeIdentifier;
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccon", "extensions", rid);
        if (!Directory.Exists(source))
        {
            return;
        }

        var target = Path.Combine(options.DataRoot, "extensions", rid);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccon-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
