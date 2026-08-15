using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Observability;
using Dapper;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     WP3 step 5. Three long-lived processes were running a binary that had already been replaced on
///     disk, and the only way to see it was <c>ps</c> against the binary's mtime. A process that
///     states its own version and the engine fingerprint of the bank it opened makes that visible in
///     the log every operator already reads.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankEngineReporterTests : IDisposable
{
    private const int StartupEventId = 13;

    private readonly string _dataRoot = TestData.CreateTempRoot("bank-engine-reporter");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<BankEngineReporter> _logger = new();

    public BankEngineReporterTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task Report_NamesTheRunningVersionAndTheBanksEngine()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, 'local:bundled')",
                new { key = EmbeddingSettingsKeys.Engine });
        }

        await new BankEngineReporter(_factory, _logger).ReportAsync(TestContext.Current.CancellationToken);

        var record = _logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(StartupEventId);
        record.Message.ShouldContain("local:bundled");
        record.Message.ShouldContain(ServerInfo.BinaryVersion);
    }

    /// <summary>
    ///     A bank whose engine was never configured is the case that produced the drift: the ingest
    ///     budget silently fell back to a mismatched counter (ADR-0063). Saying "unset" is the point —
    ///     an absent line reads as "nothing to report" rather than "nobody chose an engine".
    /// </summary>
    [Fact]
    public async Task Report_SaysUnset_WhenTheBankHasNoEngine()
    {
        await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        await new BankEngineReporter(_factory, _logger).ReportAsync(TestContext.Current.CancellationToken);

        _logger.Collector.GetSnapshot().ShouldHaveSingleItem().Message.ShouldContain("unset");
    }

    /// <summary>
    ///     Startup diagnostics must never be the reason a process fails to start. An unopenable bank
    ///     has its own error path; this one stays quiet rather than adding a second failure to it.
    /// </summary>
    [Fact]
    public async Task Report_OnAnUnreadableBank_DoesNotThrow()
    {
        var options = TestData.CreateInfrastructureOptions(Path.Combine(_dataRoot, "nope", "deeper"));
        var broken = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        File.WriteAllText(Path.Combine(_dataRoot, "blocker"), "x");

        await Should.NotThrowAsync(() =>
            new BankEngineReporter(broken, _logger).ReportAsync(TestContext.Current.CancellationToken));
    }
}
