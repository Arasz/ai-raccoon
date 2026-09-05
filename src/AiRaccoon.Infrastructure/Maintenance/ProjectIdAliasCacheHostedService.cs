using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Warms the choke-point alias cache from the durable <c>project_id_aliases</c> table once
///     per process (Package E1: "cached, reload on map change" — this is the startup leg; the
///     repair job reloads after persisting, the sync pull arm reloads after merging). Without it
///     every restart resets <see cref="ProjectIdAliasMap.Default" /> to the empty steady state and
///     P3 enforcement silently disarms: a retired id becomes writable again, an alias loser stops
///     folding through, until someone happens to run a repair or a sync pull in the new process.
///     <para>
///         Fail-open by design: a warm failure logs a warning and leaves the empty map — writes
///         behave exactly as pre-E — never blocks the server from starting. The next reload
///         opportunity (repair apply, sync pull, restart) re-attempts the warm.
///     </para>
/// </summary>
public sealed partial class ProjectIdAliasCacheHostedService(
    ISqliteConnectionFactory connectionFactory,
    IOperationTelemetry telemetry,
    ILogger<ProjectIdAliasCacheHostedService> logger) : IHostedService
{
    internal const string OperationName = "project-id-aliases.warm";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var pass = telemetry.Begin(OperationName);
        try
        {
            await using var connection = await connectionFactory.OpenBankSkippingEnsureAsync(cancellationToken)
                .ConfigureAwait(false);
            await MemorySchema.EnsureCheapAsync(connection, cancellationToken).ConfigureAwait(false);
            await ProjectIdAliases.LoadAndCacheAsync(connection, logger, cancellationToken).ConfigureAwait(false);
            // Startup-only pass: it either loaded rows worth a span or correctly found none.
            pass.NoteWork();
            pass.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            pass.Failed(ex);
            Log.WarmFailed(logger, ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static partial class Log
    {
        [LoggerMessage(EventId = 713, Level = LogLevel.Warning,
            Message = "ai-raccoon: warming the project-ids alias cache failed; P3 enforcement stays disarmed until the next reload ({Reason})")]
        public static partial void WarmFailed(ILogger logger, string reason);
    }
}
