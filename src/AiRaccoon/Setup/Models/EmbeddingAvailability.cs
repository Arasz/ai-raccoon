using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Setup.Models;

/// <summary>
///     Best-effort startup check for the bundled ONNX model (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature): warns on stderr, never fails the boot.
/// </summary>
public sealed partial class EmbeddingAvailability(ILogger<EmbeddingAvailability> logger, IBundledModel bundledModel) : IEmbeddingAvailability
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs the ensure delegate under a 30 s bound and writes a warning when assets are unavailable; never throws.</summary>
    public async Task EnsureEmbeddingAvailabilityAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            var result = await bundledModel.EnsureAsync(cts.Token).ConfigureAwait(false);
            if (!result.AllPresent)
            {
                Log.BundledEmbedingModuleUnavailable(logger, string.Join("; ", result.Errors));
            }
        }
        catch (Exception ex)
        {
            Log.BundledEmbedingModuleCheckFailed(logger, ex);
        }
    }

    public static partial class Log
    {
        [LoggerMessage(EventId = 40, Level = LogLevel.Warning, Message = "Bundled embedding model unavailable ({Errors}); run 'ai-raccoon model set local' to restore it")]
        public static partial void BundledEmbedingModuleUnavailable(ILogger logger, string errors);

        [LoggerMessage(EventId = 41, Level = LogLevel.Error, Message = "Bundled embedding model check failed; run 'ai-raccoon model set local' to restore it")]
        public static partial void BundledEmbedingModuleCheckFailed(ILogger logger, Exception exception);
    }
}
