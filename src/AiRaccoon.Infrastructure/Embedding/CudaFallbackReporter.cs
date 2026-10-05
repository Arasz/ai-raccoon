using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Reports a CUDA inference failure and the selected replacement provider.</summary>
internal sealed partial class CudaFallbackReporter(ILogger logger)
{
    public void Report(Exception exception, string executionProvider) => Log.Fallback(logger, exception, executionProvider);

    private static partial class Log
    {
        [LoggerMessage(EventId = 449, Level = LogLevel.Warning,
            Message = "CUDA inference failed; retrying this embedding with {ExecutionProvider}. Later embeddings will use the fallback session.")]
        public static partial void Fallback(ILogger logger, Exception exception, string executionProvider);
    }
}
