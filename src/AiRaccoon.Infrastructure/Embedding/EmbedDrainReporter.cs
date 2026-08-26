using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The embed drain's one log + metric surface. Both drainers report through this:
///     EmbedDrainService's bounded pump pass and EntryEmbedder's lease-held migration drain.
/// </summary>
public sealed partial class EmbedDrainReporter(IMeasurementRecorder measurements, TimeProvider timeProvider)
{
    public void PassStarted(ILogger logger, EmbedCorpus corpus) => Log.DrainStarted(logger, corpus);

    public void PassFailed(ILogger logger, EmbedCorpus corpus, Exception exception) =>
        Log.DrainFailed(logger, corpus, exception);

    public void SkippedCoalesced(ILogger logger) => Log.DrainSkippedCoalesced(logger);

    public void InvalidRowsPerRunSetting(ILogger logger, string value, int max) =>
        Log.InvalidRowsPerRunSetting(logger, value, max);

    public void SelfReSignalNotQueued(ILogger logger, EmbedCorpus corpus) =>
        Log.SelfReSignalNotQueued(logger, corpus);

    /// <summary>
    ///     The finish line and its two series — one computation, two destinations. Both call
    ///     sites land on drain.&lt;corpus&gt;.rows / .duration_ms with the same corpus tag.
    /// </summary>
    public void PassFinished(ILogger logger, EmbedCorpus corpus, int rows, TimeSpan elapsed)
    {
        Log.DrainFinished(logger, corpus, rows);
        var corpusName = corpus.ToString().ToLowerInvariant();
        var now = timeProvider.GetUtcNow();
        measurements.Record(new Measurement(MetricsConfigKeys.DrainRowsMetricName(corpusName),
            MeasurementKind.Histogram, rows, "count", now, MetricsConfigKeys.SelfMetricsProjectId));
        measurements.Record(new Measurement(MetricsConfigKeys.DrainDurationMetricName(corpusName),
            MeasurementKind.Histogram, elapsed.TotalMilliseconds, "ms", now, MetricsConfigKeys.SelfMetricsProjectId));
    }

    /// <summary>
    ///     1002-1007 moved VERBATIM from EmbedDrainService — same ids, same levels, same
    ///     templates. The whole block moves because splitting it would leave two overlapping
    ///     owners, which EventIdBlocks_DoNotInterleaveBetweenOwners forbids (LANE P4).
    /// </summary>
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

        [LoggerMessage(EventId = 1007, Level = LogLevel.Debug,
            Message = "Embed drain's self re-signal for {Corpus} did not enqueue (already queued, or the pump is full); the next poll recovers it")]
        public static partial void SelfReSignalNotQueued(ILogger logger, EmbedCorpus corpus);

    }
}
