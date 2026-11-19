using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     The watch digest's replace reports a skip and stores a skip-marked stamp: catch-up treats the
///     unchanged file as not due, and a replace still retries it because the marker matches no real hash.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SkippedFileNotFingerprintedTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-skip-fingerprint");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task ReplaceIfFileChangedAsync_UnsupportedFile_ReportsSkip_StoresNoRealHash_AndRetriesNextTime()
    {
        var provider = BuildProvider();
        var store = provider.GetRequiredService<IMemoryStore>();
        var file = Path.Combine(_dataRoot, "tool.unknownext");
        await File.WriteAllTextAsync(file, "nobody routes this", TestContext.Current.CancellationToken);
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

        var first = await store.ReplaceIfFileChangedAsync("acme", file, "same-hash", TestContext.Current.CancellationToken);

        first.Skipped.ShouldBe(SkipReason.UnsupportedType);
        (await StoredHashAsync(provider, file)).ShouldNotBe("same-hash", "a skip must never hash-skip the unchanged file");
        var second = await store.ReplaceIfFileChangedAsync("acme", file, "same-hash", TestContext.Current.CancellationToken);
        second.Replaced.ShouldBeTrue("the skip marker matches no real hash, so a digest of the unchanged file runs again");
        second.Skipped.ShouldBe(SkipReason.UnsupportedType);
    }

    [RetryFact]
    public async Task ReplaceIfFileChangedAsync_IndexedFileTurnsBinary_DropsFingerprintAndChunks_AndRestoredContentIsIndexedAgain()
    {
        var provider = BuildProvider();
        var store = provider.GetRequiredService<IMemoryStore>();
        var file = Path.Combine(_dataRoot, "notes.md");
        var original = "# Title\n\nsome indexed body text";
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, original, TestContext.Current.CancellationToken);
        (await store.ReplaceIfFileChangedAsync("acme", file, "hash-a", TestContext.Current.CancellationToken))
            .Skipped.ShouldBeNull();
        (await CountFingerprintsAsync(provider, file)).ShouldBe(1);

        await File.WriteAllTextAsync(file, "binary\0blob", TestContext.Current.CancellationToken);
        var turned = await store.ReplaceIfFileChangedAsync("acme", file, "hash-b", TestContext.Current.CancellationToken);

        turned.Skipped.ShouldBe(SkipReason.Binary);
        (await StoredHashAsync(provider, file)).ShouldNotBe("hash-a", "the old hash must not outlive the content it described");
        (await CountChunksAsync(provider, file)).ShouldBe(0);

        await File.WriteAllTextAsync(file, original, TestContext.Current.CancellationToken);
        var restored = await store.ReplaceIfFileChangedAsync("acme", file, "hash-a", TestContext.Current.CancellationToken);

        restored.Replaced.ShouldBeTrue("restoring the original content must not hash-skip");
        (await CountChunksAsync(provider, file)).ShouldBeGreaterThan(0);
        (await CountFingerprintsAsync(provider, file)).ShouldBe(1);
    }

    [RetryFact]
    public async Task ReplaceIfFileChangedAsync_SkippedFile_IsNotDueOnTheNextCatchUp_UntilItChanges()
    {
        var provider = BuildProvider();
        var store = provider.GetRequiredService<IMemoryStore>();
        var file = Path.Combine(_dataRoot, "notes.md");
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(file, "binary\0blob", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-1));

        (await store.ReplaceIfFileChangedAsync("acme", file, "hash-b", TestContext.Current.CancellationToken))
            .Skipped.ShouldBe(SkipReason.Binary);

        var watchStore = new WatchStore(provider.GetRequiredService<ISqliteConnectionFactory>(),
            provider.GetRequiredService<ISharedProjectScope>());
        var stamps = await watchStore.ListFileStampsAsync("acme", TestContext.Current.CancellationToken);
        WatchCatchUp.IsDue(file, stamps).ShouldBeFalse("an unchanged skipped file must not be digested on every catch-up pass");

        await File.WriteAllTextAsync(file, "binary\0blob grown", TestContext.Current.CancellationToken);
        WatchCatchUp.IsDue(file, stamps).ShouldBeTrue("a changed skipped file is due again");
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(_dataRoot));
        return services.BuildServiceProvider();
    }

    private static async Task<int> CountChunksAsync(IServiceProvider provider, string path)
    {
        var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM entries WHERE source_file = @path",
            new { path = IngestPath.Normalize(path) });
    }

    private static async Task<int> CountFingerprintsAsync(IServiceProvider provider, string path)
    {
        var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM watch_files WHERE path = @path",
            new { path = IngestPath.Normalize(path) });
    }

    private static async Task<string?> StoredHashAsync(IServiceProvider provider, string path)
    {
        var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string?>("SELECT file_hash FROM watch_files WHERE path = @path",
            new { path = IngestPath.Normalize(path) });
    }
}
