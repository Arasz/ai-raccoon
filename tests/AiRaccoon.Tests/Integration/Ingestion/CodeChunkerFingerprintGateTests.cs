using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     B1 (docs/work/2026-08-21-code-search-implementation-plan.md §3.4/§12.5): with the real
///     `CodeChunker` wired through production DI, a watched `.cs` file ingests real chunks and gets
///     fingerprinted end to end — the gate that used to hold this open (a zero-chunk stand-in chunker
///     must never fingerprint) now naturally stops firing because the real chunker always produces
///     &gt;=1 chunk for non-empty code. Mirrors the watch digest's own fingerprint-gated entry point
///     (`SqliteMemoryStore.ReplaceIfFileChangedAsync`), resolved from the real container rather than
///     hand-wired, so this proves the production DI graph, not just the store in isolation.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeChunkerFingerprintGateTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-fingerprint-gate");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ReplaceIfFileChangedAsync_ProdDI_WatchedCsFile_IngestsRealChunks_AndFingerprints()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        services.RegisterCoreMemoryServices(options);
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IMemoryStore>();

        var file = Path.Combine(_dataRoot, "Program.cs");
        await File.WriteAllTextAsync(file, "class Program\n{\n    static void Main() { }\n}\n",
            TestContext.Current.CancellationToken);
        await store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

        var firstDigest = await store.ReplaceIfFileChangedAsync("acme", file, "unchanged-hash",
            TestContext.Current.CancellationToken);
        firstDigest.ShouldBeTrue("no fingerprint on the file yet, so the first digest must run");

        var codeCount = await CountCodeEntriesAsync(provider, file);
        codeCount.ShouldBeGreaterThan(0, "the real CodeChunker must produce at least one chunk for non-empty code");

        var secondDigest = await store.ReplaceIfFileChangedAsync("acme", file, "unchanged-hash",
            TestContext.Current.CancellationToken);
        secondDigest.ShouldBeFalse(
            "chunking succeeded and the fingerprint is now recorded (B1), so an unchanged hash hash-skips");
    }

    private async Task<int> CountCodeEntriesAsync(IServiceProvider provider, string path)
    {
        var factory = provider.GetRequiredService<ISqliteConnectionFactory>();
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM code_entries WHERE path = @path",
            new { path = IngestPath.Normalize(path) });
    }
}
