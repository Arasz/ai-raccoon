using AiRaccoon.Core.EventPump;
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
///         signal arriving mid-drain queues its own fresh pass), open a connection, drain up to
///         <see cref="RowsPerRun" /> rows for that corpus, loop. It never re-enqueues itself when
///         rows remain — the pace is exactly today's 128-rows-per-signal; a large backlog drains
///         over several signals (the 15s on-demand poll plus one per digest/ingest), not in one
///         inference-pool-saturating burst.
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
    IOperationTelemetry telemetry,
    ILogger<EmbedDrainService> logger)
    : BackgroundService
{
    /// <summary>The topic's fixed channel bound — see <see cref="PumpTopic.Ceiling" />. Item space is 2 (<see cref="EmbedCorpus" />); 8 is slack, not a queue.</summary>
    internal const int PumpCeiling = 8;

    /// <summary>The topic's starting effective cap — see <see cref="PumpTopic.Capacity" />. No setting adjusts this; it is not the pacing lever (rows-per-run is).</summary>
    internal const int PumpCapacity = 8;

    /// <summary>
    ///     Rows drained per signal, for both corpora — today's 4 generator batches
    ///     (<see cref="EntryEmbedder.BatchSize" /> == <see cref="CodeEmbedder.BatchSize" /> == 32),
    ///     unchanged from the two jobs this consumer replaced. WP11-C makes this a bank setting
    ///     (`maintenance.embed-rows-per-run.global`); B2 keeps it a constant.
    /// </summary>
    internal const int RowsPerRun = 4 * EntryEmbedder.BatchSize;

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

    /// <summary>One drain pass for one request: open a connection, drain up to <see cref="RowsPerRun" /> rows for its corpus. Test seam.</summary>
    internal async Task DrainOnceAsync(EmbedDrainRequest request, CancellationToken cancellationToken)
    {
        Log.DrainStarted(logger, request.Corpus);
        using var pass = telemetry.Begin(OperationName);
        try
        {
            await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            var drained = request.Corpus == EmbedCorpus.Code
                ? await codeEmbedder.EmbedPendingBatchAsync(connection, RowsPerRun, cancellationToken).ConfigureAwait(false)
                : await entryEmbedder.EmbedPendingBatchAsync(connection, RowsPerRun, cancellationToken).ConfigureAwait(false);

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
    }
}
