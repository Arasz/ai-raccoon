using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Caps MLX's free-buffer cache through the <c>libmlxc.dylib</c> shipped beside the MLX plugin
///     (ADR-0114). Uncapped, the cache keeps buffers for every row length it has run and grows to
///     MLX's memory limit. Best effort: a failure is logged and the session runs uncapped.
/// </summary>
internal sealed partial class MlxCacheLimit(ILogger logger)
{
    /// <summary>The cap the MLX session applies: holds peak footprint near 1.2 GB with 64-token buckets.</summary>
    public const long DefaultBytes = 512L * 1024 * 1024;

    private const string LibraryFileName = "libmlxc.dylib";
    private const string ExportName = "mlx_set_cache_limit";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetCacheLimit(out nuint previous, nuint limit);

    /// <summary>Sets the cap in this process; true when MLX accepted it. Call on the MLX session's thread.</summary>
    public bool TryApply(string pluginDirectory, long bytes)
    {
        var path = Path.Combine(pluginDirectory, LibraryFileName);
        if (!NativeLibrary.TryLoad(path, out var library))
        {
            Log.NotApplied(logger, bytes >> 20, $"{path} could not be loaded");
            return false;
        }

        if (!NativeLibrary.TryGetExport(library, ExportName, out var export))
        {
            Log.NotApplied(logger, bytes >> 20, $"{ExportName} is not exported by {path}");
            return false;
        }

        var setCacheLimit = Marshal.GetDelegateForFunctionPointer<SetCacheLimit>(export);
        if (setCacheLimit(out var previous, (nuint)bytes) != 0)
        {
            Log.NotApplied(logger, bytes >> 20, $"{ExportName} returned an error");
            return false;
        }

        Log.Applied(logger, bytes >> 20, (long)(previous >> 20));
        return true;
    }

    public static partial class Log
    {
        [LoggerMessage(EventId = 434, Level = LogLevel.Information,
            Message = "MLX buffer cache capped at {LimitMiB} MiB (was {PreviousMiB} MiB)")]
        public static partial void Applied(ILogger logger, long limitMiB, long previousMiB);

        [LoggerMessage(EventId = 435, Level = LogLevel.Warning,
            Message = "MLX buffer cache could not be capped at {LimitMiB} MiB: {Reason}. The session runs, but its "
                      + "footprint can grow to MLX's memory limit.")]
        public static partial void NotApplied(ILogger logger, long limitMiB, string reason);
    }
}
