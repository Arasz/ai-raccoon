using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Observability;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Observability;

/// <summary>
///     A read refused for an unregistered id leaves no trace under that id: no measurement and no
///     search_quality row carry it, while the call is still counted under the bounded "refused"
///     sentinel.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ObservabilityCollection.Name)]
public sealed class RefusedReadTelemetryTests
{
    [RetryFact]
    public async Task RefusedSearchAndStats_LeaveNoMeasurementOrQualityRowUnderTheId()
    {
        var ct = TestContext.Current.CancellationToken;
        var refusedId = Guid.CreateVersion7().ToString("D");
        await using var envGate = await TestData.HoldEnvGateAsync(ct);
        var dataRoot = TestData.CreateTempRoot("refused-read-telemetry");
        try
        {
            using var lease = LoopbackPort.Reserve();
            var host = TelemetryServerHost.Create(dataRoot, lease.Port);
            var buffer = host.Services.GetRequiredService<IMeasurementBuffer>();
            var measurements = new List<Measurement>();
            var refused = new List<bool?>();

            lease.ReleaseForBind();
            await host.StartAsync(ct);
            try
            {
                await using var client = await TelemetryServerHost.ConnectAsync(lease.Port, ct);
                var search = await client.CallToolAsync("memory_search",
                    new Dictionary<string, object?> { ["projectId"] = refusedId, ["query"] = "x", ["sessionId"] = "sess-refused" },
                    cancellationToken: ct);
                var stats = await client.CallToolAsync("memory_stats",
                    new Dictionary<string, object?> { ["projectId"] = refusedId }, cancellationToken: ct);
                refused.AddRange([search.IsError, stats.IsError]);
            }
            finally
            {
                // Drained before StopAsync: the flusher's StopAsync would otherwise take what is left.
                measurements.AddRange(buffer.DrainAll());
                await host.StopAsync(ct);
            }

            measurements.ShouldNotContain(m => m.ProjectId == refusedId);
            measurements.ShouldContain(m => m.Name == "memory_search" && m.ProjectId == "refused");
            measurements.ShouldContain(m => m.Name == "memory_stats" && m.ProjectId == "refused");
            (await QualityRowsAsync(dataRoot, refusedId, ct)).ShouldBe(0);
            refused.ShouldAllBe(isError => isError == true);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private static async Task<long> QualityRowsAsync(string dataRoot, string projectId, CancellationToken ct)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        await using var connection = await factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM search_quality WHERE project_id = @projectId", new { projectId }, cancellationToken: ct));
    }
}
