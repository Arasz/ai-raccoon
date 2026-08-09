using System.Diagnostics;
using System.Diagnostics.Metrics;
using AiRaccoon.Core.Observability;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Observability;

/// <summary>
///     The IOperationTelemetry port over the "AiRaccoon.Background" Meter and ActivitySource:
///     one span plus one duration and one pass measurement per background pass (WP13,
///     docs/work/2026-08-09-otlp-fix-plan.md). Creating a Meter starts no thread, so ADR-0009's
///     zero-threads-when-unconfigured guarantee holds.
/// </summary>
public sealed class BackgroundTelemetry : IOperationTelemetry, IDisposable
{
    private const string OperationTag = "operation";
    private const string ResultTag = "result";
    private const string ErrorTypeTag = "error.type";
    private const string ResultSuccess = "success";
    private const string ResultError = "error";
    private const string ResultUnknown = "unknown";

    private readonly Counter<long> _passes;
    private readonly Histogram<double> _duration;

    public BackgroundTelemetry()
    {
        Meter = new Meter(OtlpNames.BackgroundScope);
        ActivitySource = new ActivitySource(OtlpNames.BackgroundScope);
        _passes = Meter.CreateCounter<long>(
            OtlpNames.BackgroundPasses,
            unit: "{pass}",
            description: "Background passes, by operation and result");

        // Background passes run from milliseconds (an idle check) to minutes (a vacuum), so the
        // boundaries reach further out than the tool histogram's.
        var advice = new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 300, 900]
        };
        _duration = Meter.CreateHistogram<double>(
            OtlpNames.BackgroundPassDuration,
            "s",
            "Duration of a background pass",
            null,
            advice);
    }

    /// <summary>Meter named "AiRaccoon.Background" — discoverable by dotnet-counters via EventPipe.</summary>
    public Meter Meter { get; }

    /// <summary>ActivitySource named "AiRaccoon.Background" — a pass is not a tool call.</summary>
    public ActivitySource ActivitySource { get; }

    public IOperationScope Begin(string operation)
    {
        Guard.IsNotNullOrWhiteSpace(operation);
        return new Scope(this, operation);
    }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }

    private void Record(string operation, TimeSpan elapsed, string result, string? errorType)
    {
        var tags = new TagList { { OperationTag, operation }, { ResultTag, result } };
        if (errorType is not null)
        {
            tags.Add(ErrorTypeTag, errorType);
        }

        _passes.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
    }

    private sealed class Scope : IOperationScope
    {
        private readonly BackgroundTelemetry _owner;
        private readonly string _operation;
        private readonly Activity? _activity;
        private readonly long _startedAt;
        private bool _recorded;

        public Scope(BackgroundTelemetry owner, string operation)
        {
            _owner = owner;
            _operation = operation;
            _activity = owner.ActivitySource.StartActivity(operation, ActivityKind.Internal);
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Tag(string key, string value) => _activity?.SetTag(key, value);

        public void Succeeded()
        {
            if (Take())
            {
                _activity?.SetStatus(ActivityStatusCode.Ok);
                _activity?.SetTag(ResultTag, ResultSuccess);
                _owner.Record(_operation, Elapsed, ResultSuccess, null);
            }
        }

        public void Failed(Exception exception)
        {
            Guard.IsNotNull(exception);
            if (!Take())
            {
                return;
            }

            var errorType = exception.GetType().Name;
            _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            _activity?.SetTag(ResultTag, ResultError);
            _activity?.SetTag(ErrorTypeTag, errorType);
            _activity?.AddException(exception);
            _owner.Record(_operation, Elapsed, ResultError, errorType);
        }

        public void Dispose()
        {
            if (Take())
            {
                _activity?.SetTag(ResultTag, ResultUnknown);
                _owner.Record(_operation, Elapsed, ResultUnknown, null);
            }

            _activity?.Dispose();
        }

        private TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedAt);

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
    }
}
