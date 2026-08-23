using System.Diagnostics;
using System.Diagnostics.Metrics;
using AiRaccoon.Core.Observability;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Observability;

/// <summary>
///     The IOperationTelemetry port over the "AiRaccoon.Background" Meter and ActivitySource: one
///     duration and one pass measurement always, a span only when NoteWork() was called or the
///     outcome is failure/unknown (WP13 span-volume fix, docs/work/2026-08-09-otlp-fix-plan.md).
///     Creating a Meter starts no thread, so ADR-0009's zero-threads-when-unconfigured guarantee holds.
/// </summary>
public sealed class BackgroundTelemetry : IOperationTelemetry, IDisposable
{
    private const string OperationTag = "operation";
    private const string ResultTag = "result";
    private const string ErrorTypeTag = "error.type";
    private const string FailuresTag = "failures";
    private const string ResultSuccess = "success";
    private const string ResultError = "error";
    private const string ResultPartial = "partial";
    private const string ResultUnknown = "unknown";
    private readonly Histogram<double> _duration;

    private readonly Counter<long> _passes;

    private readonly Histogram<long> _rows;

    public BackgroundTelemetry()
    {
        Meter = new Meter(OtlpNames.BackgroundScope);
        ActivitySource = new ActivitySource(OtlpNames.BackgroundScope);
        _passes = Meter.CreateCounter<long>(
            OtlpNames.BackgroundPasses,
            "{pass}",
            "Background passes, by operation and result");

        // Background passes run from milliseconds (an idle check) to minutes (a vacuum), so the
        // boundaries reach further out than the tool histogram's.
        var advice = new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 300, 900]
        };
        _duration = Meter.CreateHistogram(
            OtlpNames.BackgroundPassDuration,
            "s",
            "Duration of a background pass",
            null,
            advice);

        _rows = Meter.CreateHistogram<long>(
            OtlpNames.BackgroundPassRows,
            "{row}",
            "Rows processed in a background pass, for passes that have a natural row count");
    }

    /// <summary>Meter named "AiRaccoon.Background" — discoverable by dotnet-counters via EventPipe.</summary>
    public Meter Meter { get; }

    /// <summary>ActivitySource named "AiRaccoon.Background" — a pass is not a tool call.</summary>
    public ActivitySource ActivitySource { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }

    public IOperationScope Begin(string operation)
    {
        Guard.IsNotNullOrWhiteSpace(operation);
        return new Scope(this, operation);
    }

    private void Record(string operation, TimeSpan elapsed, string result, string? errorType, long? rows)
    {
        var tags = new TagList { { OperationTag, operation }, { ResultTag, result } };
        if (errorType is not null)
        {
            tags.Add(ErrorTypeTag, errorType);
        }

        _passes.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
        if (rows is { } value)
        {
            _rows.Record(value, tags);
        }
    }

    private sealed class Scope(BackgroundTelemetry owner, string operation) : IOperationScope
    {
        private readonly List<KeyValuePair<string, string>> _pendingTags = [];
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
        private Activity? _activity;
        private bool _recorded;
        private bool _worthy;
        private long? _rows;

        private TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedAt);

        public void Tag(string key, string value)
        {
            if (_activity is { } activity)
            {
                activity.SetTag(key, value);
                return;
            }

            _pendingTags.Add(new KeyValuePair<string, string>(key, value));
        }

        public void NoteWork() => _worthy = true;

        /// <summary>
        ///     Throws if the scope's one measurement was already recorded (#548 review, B1): a
        ///     silently-dropped rows value is exactly the bug that shipped — the caller must record
        ///     rows before <see cref="Succeeded" />/<see cref="Failed" />/<see cref="PartiallyFailed" />,
        ///     never after.
        /// </summary>
        public void RecordRows(long rows)
        {
            if (_recorded)
            {
                throw new InvalidOperationException(
                    $"{nameof(RecordRows)} was called after the scope's measurement was already recorded " +
                    "(Succeeded/Failed/PartiallyFailed/Dispose) — the row count would be silently dropped. " +
                    $"Call {nameof(RecordRows)} before the terminal call, not after.");
            }

            _rows = rows;
        }

        public void Succeeded()
        {
            if (!Take())
            {
                return;
            }

            if (_worthy)
            {
                StartSpan();
            }

            _activity?.SetStatus(ActivityStatusCode.Ok);
            _activity?.SetTag(ResultTag, ResultSuccess);
            owner.Record(operation, Elapsed, ResultSuccess, null, _rows);
        }

        public void Failed(Exception exception)
        {
            Guard.IsNotNull(exception);
            if (!Take())
            {
                return;
            }

            StartSpan(); // a failure is always worth reading, no-op pass or not
            var errorType = exception.GetType().Name;
            _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            _activity?.SetTag(ResultTag, ResultError);
            _activity?.SetTag(ErrorTypeTag, errorType);
            _activity?.AddException(exception);
            owner.Record(operation, Elapsed, ResultError, errorType, _rows);
        }

        public void PartiallyFailed(int failureCount)
        {
            Guard.IsGreaterThan(failureCount, 0);
            if (!Take())
            {
                return;
            }

            StartSpan(); // some work failed: always worth reading, no-op pass or not
            _activity?.SetStatus(ActivityStatusCode.Ok);
            _activity?.SetTag(ResultTag, ResultPartial);
            _activity?.SetTag(FailuresTag, failureCount.ToString());
            owner.Record(operation, Elapsed, ResultPartial, null, _rows);
        }

        public void Dispose()
        {
            if (Take())
            {
                StartSpan(); // an abandoned pass is a hole, not a success: always worth reading
                _activity?.SetTag(ResultTag, ResultUnknown);
                owner.Record(operation, Elapsed, ResultUnknown, null, _rows);
            }

            _activity?.Dispose();
        }

        /// <summary>Claims the one measurement this scope is allowed to record.</summary>
        private bool Take()
        {
            if (_recorded)
            {
                return false;
            }

            _recorded = true;
            return true;
        }

        /// <summary>
        ///     Materializes the span, backdated to when the pass actually started, and
        ///     applies any tags recorded before the pass was known to be worth reading.
        /// </summary>
        private void StartSpan()
        {
            _activity = owner.ActivitySource.StartActivity(operation, ActivityKind.Internal,
                default(ActivityContext), null, null, _startedAtUtc);
            if (_activity is null)
            {
                return;
            }

            foreach (var tag in _pendingTags)
            {
                _activity.SetTag(tag.Key, tag.Value);
            }
        }
    }
}
