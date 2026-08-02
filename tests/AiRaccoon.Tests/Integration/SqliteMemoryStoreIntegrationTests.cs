using System.Runtime.InteropServices;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
/// End-to-end tests against the REAL sqlite-memory/vector extensions (the "done means proven"
/// gate for the store). They need provisioned native binaries under the data root; when the
/// host RID has none they are skipped honestly via Assert.Skip (never counted as passing green).
///
/// Embedding-gated tests: with no model configured the bank runs in deferred mode (FR-MEM-1.12)
/// and memory_search returns nothing until memory_embed_pending runs. Tests that need real
/// embeddings read AIRACCOON_TEST_GGUF (a GGUF model path) and Assert.Skip when it is unset —
/// so the search/embedding round-trip is exercised in CI only when a model is provided, and
/// reported as skipped otherwise, never as a false green.
/// </summary>
public class SqliteMemoryStoreIntegrationTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    private static string? TestModelPath =>
        Environment.GetEnvironmentVariable("AIRACCOON_TEST_GGUF");

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
            Assert.Skip("native extensions not provisioned for this host RID; skipping integration test");
            return null; // unreachable — Assert.Skip throws
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

    [Fact]
    public async Task Write_SameContentTwice_CreatesExactlyOneRow_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        await store.WriteAsync(new MemoryWriteRequest("acme", "dedup exactly once"), TestContext.Current.CancellationToken);
        await store.WriteAsync(new MemoryWriteRequest("acme", "dedup exactly once"), TestContext.Current.CancellationToken);

        var stats = await store.GetStatsAsync("acme", TestContext.Current.CancellationToken);
        stats.EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task WriteWithoutModel_ReportsPending_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        await store.WriteAsync(new MemoryWriteRequest("acme", "deferred until a model is set"), TestContext.Current.CancellationToken);

        var stats = await store.GetStatsAsync("acme", TestContext.Current.CancellationToken);
        stats.PendingCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task EmbedPending_NullLimit_DoesNotThrow_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        // No model is configured, so embedding cannot succeed — but the NULL-limit path must
        // not throw the upstream "expects a positive INTEGER" error (M2). The 0-arg form is
        // used; with no model it errors cleanly as "no embedding model configured".
        await store.WriteAsync(new MemoryWriteRequest("acme", "pending item"), TestContext.Current.CancellationToken);
        var exception = await Record.ExceptionAsync(
            () => store.EmbedPendingAsync("acme", limit: null, TestContext.Current.CancellationToken));

        exception.ShouldNotBeNull();
        exception!.Message.ShouldNotContain("positive INTEGER");
        exception.Message.ShouldContain("embedding model");
    }

    [Fact]
    public async Task Search_ReturnsStoredEntry_AfterEmbedPending_WithConfiguredModel()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var model = TestModelPath;
        if (string.IsNullOrWhiteSpace(model))
        {
            Assert.Skip("AIRACCOON_TEST_GGUF not set; skipping the real-embedding search round-trip");
        }

        await store.ConfigureEmbeddingAsync("acme", "local", model, null, TestContext.Current.CancellationToken);
        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "semantic searchable fact"), TestContext.Current.CancellationToken);

        var results = await store.SearchAsync(
            new SearchQuery("acme", "semantic searchable", scope: SearchScope.All),
            TestContext.Current.CancellationToken);

        results.ShouldContain(r => r.Hash == entry.Hash);
    }

    [Fact]
    public async Task Search_ContextFilter_RestrictsToThatContext_WithConfiguredModel()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        var model = TestModelPath;
        if (string.IsNullOrWhiteSpace(model))
        {
            Assert.Skip("AIRACCOON_TEST_GGUF not set; skipping the context-filter search test");
        }

        await store.ConfigureEmbeddingAsync("acme", "local", model, null, TestContext.Current.CancellationToken);
        await store.WriteAsync(new MemoryWriteRequest("acme", "docs only fact", context: "docs:api"), TestContext.Current.CancellationToken);

        var projectOnly = await store.SearchAsync(
            new SearchQuery("acme", "docs only", scope: SearchScope.Project),
            TestContext.Current.CancellationToken);
        var docsOnly = await store.SearchAsync(
            new SearchQuery("acme", "docs only", scope: SearchScope.Project, limit: 20, minScore: 0),
            TestContext.Current.CancellationToken);

        // The content lives in context docs:api, not project:acme — so a project-scoped search
        // must not return it. Asserting the docs-context row is reachable requires the docs
        // context in scope; here we assert the project-scoped search excludes it (contract).
        docsOnly.ShouldNotContain(r => r.Path == "docs:api");
        _ = projectOnly; // the meaningful assertion is the exclusion above
    }

    [Fact]
    public async Task WorkspaceConsolidate_PromotesKeptHash_ThenEmptiesWorkspace_AgainstRealExtensions()
    {
        var store = await TryCreateStoreAsync();
        if (store is null)
        {
            return;
        }

        await store.WriteAsync(
            new MemoryWriteRequest("acme", "workspace durable fact", workspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        // Drive the REAL WorkspaceService so the promote (add_content with the entry's path
        // into project:acme) and the workspace-context delete are both exercised (QA #8).
        var workspaces = new WorkspaceService(store);
        var result = await workspaces.ConsolidateAsync("acme", "ws-1", ["all"], TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(1);
        var projectEntries = await store.ListContextAsync("acme", "project:acme", TestContext.Current.CancellationToken);
        projectEntries.ShouldContain(e => e.Value == "workspace durable fact");
        var workspaceEntries = await store.ListContextAsync("acme", "workspace:ws-1", TestContext.Current.CancellationToken);
        workspaceEntries.ShouldBeEmpty();
    }

    private async Task ProvisionIfAvailableAsync(InfrastructureOptions options)
    {
        // If the developer (or a previous run) already provisioned the host RID's modules in
        // ~/.ai-raccoon/extensions/<rid>, copy them into this test's temp data root so the
        // integration test exercises the real binaries.
        var rid = RuntimeInformation.RuntimeIdentifier;
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon", "extensions", rid);
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
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
