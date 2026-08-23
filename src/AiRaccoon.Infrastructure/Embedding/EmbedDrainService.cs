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
///         corpus (WP11-C; default 128, re-read on every pass). A pass that consumes the full row
///         budget re-signals its own corpus (WP1, docs/work/2026-08-23-post-delta-4-plan.md) so the
///         next pass runs immediately instead of waiting out the 15s on-demand poll — a full budget
///         is real progress (<c>EmbedPendingBatchAsync</c> counts only rows whose UPDATE landed), so
///         the re-signal cannot spin once the backlog is actually exhausted. A partial pass does not
///         re-signal.
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

            // A full row budget means the backlog may not be empty — EmbedPendingBatchAsync counts
            // only rows whose UPDATE landed, so this is real progress and cannot spin once the
            // corpus is actually exhausted. Re-signal instead of waiting out the 15s poll (WP1).
            if (drained >= rowsPerRun)
            {
                pump.TryEnqueue(request);
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

    /// <summary>The most recently warned-about invalid raw value, or null once a valid one is seen — this pass runs every drain, so warning on every pass for a persistent bad value would never stop (review finding 2, #517).</summary>
    private string? _lastWarnedRowsPerRun;

    /// <summary>Unset defaults to 128; a present-but-invalid value (unparseable, non-positive, or over the ceiling) also falls back, warning once per DISTINCT bad value — not every pass. Test seam.</summary>
    internal int ResolveRowsPerRun(string? raw)
    {
        if (!BankMaintenanceConfigKeys.TryParseEmbedRowsPerRun(raw, out var rows))
        {
            if (raw != _lastWarnedRowsPerRun)
            {
                Log.InvalidRowsPerRunSetting(logger, raw!, BankMaintenanceConfigKeys.MaxEmbedRowsPerRun);
                _lastWarnedRowsPerRun = raw;
            }
        }
        else
        {
            _lastWarnedRowsPerRun = null; // a value that goes bad again after being fixed warns again
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
            Message = "Invalid maintenance.embed-rows-per-run.global setting '{Value}': expected a positive integer, at most {Max}. Falling back to the default or clamped value.")]
        public static partial void InvalidRowsPerRunSetting(ILogger logger, string value, int max);
    }
}
