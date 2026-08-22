using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The embed topic's single consumer (docs/work/2026-08-22-post-delta-3-plan.md WP11-B2, owner
///     ruling G17): one reader on one <see cref="IEventPump{T}" />, so the ONNX inference pool has
///     exactly one caller in the process regardless of how many producers signal it —
///     <see cref="PendingEmbedJob" />, <see cref="CodeReindexJob" />, a watch digest, or a directory
///     ingest. No lease, no <c>'embedding'</c> state, no semaphore: a single reader is single-file
///     by construction.
///     <para>
///         Loop: wait for a signal, take exactly one queued request (releasing its coalesce key so a
///         signal arriving mid-drain queues its own fresh pass), open a connection, drain up to the
///         bank's <see cref="BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal" /> rows for that
///         corpus, loop (WP11-C; default 128, re-read on every pass). It never re-enqueues itself
///         when rows remain — a large backlog drains over several signals (the 15s on-demand poll
///         plus one per digest/ingest), not in one inference-pool-saturating burst.
///     </para>
///     <para>
///         The channel is a wake-up, not the record: <c>embed_state = 'pending'</c> (ADR-0076) is
///         the durable outbox, so a signal dropped because the pump is full costs at most one poll
///         interval of latency and zero rows.
///     </para>
/// </summary>
public sealed partial class EmbedDrainService(
    IEventPump<EmbedDrainRequest> pump,
    ISqliteConnectionFactory factory,
    IEntryEmbedder entryEmbedder,
    ICodeEmbedder codeEmbedder,
    ISettingsStore settings,
    IOperationTelemetry telemetry,
    ILogger<EmbedDrainService> logger)
    : BackgroundService
{
    /// <summary>The topic's fixed channel bound — see <see cref="PumpTopic.Ceiling" />. Item space is 2 (<see cref="EmbedCorpus" />); 8 is slack, not a queue.</summary>
    internal const int PumpCeiling = 8;

    /// <summary>The topic's starting effective cap — see <see cref="PumpTopic.Capacity" />. No setting adjusts this; it is not the pacing lever (rows-per-run is).</summary>
    internal const int PumpCapacity = 8;

    internal const string OperationName = "embed.drain";

    /// <summary>Completed after each drain pass (success, failure, or a signal that raced to an
    /// empty pump); test seam.</summary>
    internal TickSignal Drains { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pump.WaitForItemAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Exactly one item: a signal is a wake-up for the WHOLE topic, and the coalesce key is
            // released here (EventPump<T>.DrainUpTo), not at completion — a change landing mid-drain
            // queues its own fresh pass instead of folding into rows this pass already read.
            var taken = pump.DrainUpTo(1);
            if (taken.Count == 0)
            {
                // Single reader: nothing else can have taken it first. Only reachable if the
                // channel reported readable and then had nothing — logged so a real occurrence is
                // visible rather than silently swallowed.
                Log.DrainSkippedCoalesced(logger);
                Drains.Increment();
                continue;
            }

            try
            {
                await DrainOnceAsync(taken[0], stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.DrainFailed(logger, taken[0].Corpus, ex);
            }
            finally
            {
                Drains.Increment();
            }
        }
    }

    /// <summary>One drain pass for one request: open a connection, drain up to the configured rows-per-run for its corpus. Test seam.</summary>
    internal async Task DrainOnceAsync(EmbedDrainRequest request, CancellationToken cancellationToken)
    {
        Log.DrainStarted(logger, request.Corpus);
        using var pass = telemetry.Begin(OperationName);
        try
        {
            var raw = await settings.GetSettingAsync(BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal, cancellationToken)
                .ConfigureAwait(false);
            var rowsPerRun = ResolveRowsPerRun(raw);
            await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            var drained = request.Corpus == EmbedCorpus.Code
                ? await codeEmbedder.EmbedPendingBatchAsync(connection, rowsPerRun, cancellationToken).ConfigureAwait(false)
                : await entryEmbedder.EmbedPendingBatchAsync(connection, rowsPerRun, cancellationToken).ConfigureAwait(false);

            if (drained > 0)
            {
                pass.NoteWork();
            }

            pass.Succeeded();
            Log.DrainFinished(logger, request.Corpus, drained);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            pass.Failed(ex);
            throw;
        }
    }

    /// <summary>Unset defaults to 128; a present-but-unparseable value also defaults, but warns once. Test seam.</summary>
    internal int ResolveRowsPerRun(string? raw)
    {
        if (!BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun(raw, out var rows))
        {
            Log.InvalidRowsPerRunSetting(logger, raw!);
        }

        return rows;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Embed drain pass started for {Corpus}")]
        public static partial void DrainStarted(ILogger logger, EmbedCorpus corpus);

        [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
            Message = "Embed drain pass finished for {Corpus}: {Rows} row(s)")]
        public static partial void DrainFinished(ILogger logger, EmbedCorpus corpus, int rows);

        [LoggerMessage(EventId = 1004, Level = LogLevel.Debug,
            Message = "Embed drain signalled but the pump was already empty when taken (coalesced away)")]
        public static partial void DrainSkippedCoalesced(ILogger logger);

        [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Embed drain pass failed for {Corpus}")]
        public static partial void DrainFailed(ILogger logger, EmbedCorpus corpus, Exception exception);

        [LoggerMessage(EventId = 1006, Level = LogLevel.Warning,
            Message = "Invalid maintenance.embed-rows-per-run.global setting '{Value}': expected a positive integer. Using the default instead.")]
        public static partial void InvalidRowsPerRunSetting(ILogger logger, string value);
    }
}
