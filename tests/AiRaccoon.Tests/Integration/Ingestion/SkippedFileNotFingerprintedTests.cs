using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>The watch digest's replace reports a skip and writes no fingerprint, so the next digest retries the file.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SkippedFileNotFingerprintedTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-skip-fingerprint");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task ReplaceIfFileChangedAsync_UnsupportedFile_ReportsSkip_WritesNoFingerprint_AndRetriesNextTime()
    {
        var provider = BuildProvider();
        var store = provider.GetRequiredService<IMemoryStore>();
        var file = Path.Combine(_dataRoot, "tool.unknownext");
        await File.WriteAllTextAsync(file, "nobody routes this", TestContext.Current.CancellationToken);
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

        var first = await store.ReplaceIfFileChangedAsync("acme", file, "same-hash", TestContext.Current.CancellationToken);

        first.Skipped.ShouldBe(SkipReason.UnsupportedType);
        (await CountFingerprintsAsync(provider, file)).ShouldBe(0);
        var second = await store.ReplaceIfFileChangedAsync("acme", file, "same-hash", TestContext.Current.CancellationToken);
        second.Replaced.ShouldBeTrue("no fingerprint was written, so the unchanged file is digested again");
        second.Skipped.ShouldBe(SkipReason.UnsupportedType);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(_dataRoot));
        return services.BuildServiceProvider();
    }

    private static async Task<int> CountFingerprintsAsync(IServiceProvider provider, string path)
    {
        var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM watch_files WHERE path = @path",
            new { path = IngestPath.Normalize(path) });
    }
}
