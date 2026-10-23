using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Owns the process-wide ONNX Runtime environment and its log callback, and builds sessions that log one
///     node-placement line each instead of ORT's generic "Some nodes were not assigned" warning.
/// </summary>
internal sealed partial class OrtSessionLog
{
    private const string EnvironmentLogId = "ai-raccoon";

    private static readonly Lock EnvironmentGate = new();
    private static readonly ConcurrentDictionary<string, OrtSessionSink> Sinks = new(StringComparer.Ordinal);
    private static bool _callbackInstalled;
    private static long _sessionCount;

    private OrtSessionLog()
    {
    }

    /// <summary>The ORT environment, created with this class's log callback unless something else created it first.
    /// Use it instead of <see cref="OrtEnv.Instance" />, which creates an environment without the callback.</summary>
    internal static OrtEnv Environment
    {
        get
        {
            lock (EnvironmentGate)
            {
                if (!OrtEnv.IsCreated)
                {
                    var options = new EnvironmentCreationOptions
                    {
                        logId = EnvironmentLogId,
                        logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
                        loggingFunction = OnLog
                    };
                    OrtEnv.CreateInstanceWithOptions(ref options);
                    _callbackInstalled = true;
                }
            }

            return OrtEnv.Instance();
        }
    }

    /// <summary>Builds a session and logs where its nodes were placed. Overwrites the options' log id and, when the
    /// callback is installed, raises their log severity to verbose: ORT reports placement only at verbose.</summary>
    internal static InferenceSession Create(string modelPath, SessionOptions options, ILogger logger)
    {
        _ = Environment;
        var sink = new OrtSessionSink(logger);
        var logId = $"{EnvironmentLogId}-session-{Interlocked.Increment(ref _sessionCount)}";
        options.LogId = logId;
        if (_callbackInstalled)
        {
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
            Sinks[logId] = sink;
        }

        try
        {
            var session = new InferenceSession(modelPath, options);
            sink.Finish(modelPath);
            return session;
        }
        finally
        {
            Sinks.TryRemove(logId, out _);
        }
    }

    /// <summary>Routes a line to the sink of the session being built; anything else at warning or above goes to
    /// stderr, as ORT's own default sink did. Never throws back into native code.</summary>
    private static void OnLog(IntPtr param, OrtLoggingLevel severity, string category, string logId, string codeLocation, string message)
    {
        try
        {
            if (Sinks.TryGetValue(logId, out var sink))
            {
                sink.Add(severity, codeLocation, message);
            }
            else if (severity >= OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING)
            {
                Console.Error.WriteLine($"[{severity}:onnxruntime:{logId}, {codeLocation}] {message}");
            }
        }
        catch (Exception)
        {
            // A logging failure must not unwind through ORT's native frames.
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 450, Level = LogLevel.Information,
            Message = "ONNX Runtime node placement for {Model}: {Placement}")]
        public static partial void NodePlacement(ILogger logger, string model, string placement);

        [LoggerMessage(EventId = 451, Level = LogLevel.Information,
            Message = "ONNX Runtime did not report node placement for {Model}")]
        public static partial void PlacementNotReported(ILogger logger, string model);

        [LoggerMessage(EventId = 452, Message = "ONNX Runtime: {Message} ({Location})")]
        public static partial void Forwarded(ILogger logger, LogLevel level, string message, string location);
    }
}

/// <summary>Collects one session's ORT log lines while the session is built.</summary>
internal sealed class OrtSessionSink(ILogger logger)
{
    private const string PlacementCheck = "VerifyEachNodeIsAssignedToAnEp";

    private readonly Lock _gate = new();
    private readonly NodePlacement _placement = new();

    /// <summary>Takes one ORT line: placement lines feed the summary, the generic placement warning and CoreML's
    /// capability line are dropped, and other warnings and errors go to the logger.</summary>
    public void Add(OrtLoggingLevel severity, string codeLocation, string message)
    {
        lock (_gate)
        {
            _placement.Add(message);
        }

        if (severity < OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
            || codeLocation.Contains(PlacementCheck, StringComparison.Ordinal)
            || NodePlacement.IsCoreMlCapability(message))
        {
            return;
        }

        OrtSessionLog.Log.Forwarded(logger, Level(severity), message, codeLocation);
    }

    /// <summary>Logs the placement summary, or that ORT reported none.</summary>
    public void Finish(string modelPath)
    {
        string? summary;
        lock (_gate)
        {
            summary = _placement.Summary();
        }

        var model = ModelName(modelPath);
        if (summary is null)
        {
            OrtSessionLog.Log.PlacementNotReported(logger, model);
            return;
        }

        OrtSessionLog.Log.NodePlacement(logger, model, summary);
    }

    private static string ModelName(string modelPath)
    {
        var directory = Path.GetFileName(Path.GetDirectoryName(modelPath));
        var file = Path.GetFileName(modelPath);
        return string.IsNullOrEmpty(directory) ? file : $"{directory}/{file}";
    }

    private static LogLevel Level(OrtLoggingLevel severity) => severity switch
    {
        OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING => LogLevel.Warning,
        OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR => LogLevel.Error,
        _ => LogLevel.Critical
    };
}
