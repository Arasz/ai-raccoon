using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Setup;

/// <summary>
///     Best-effort startup check for the bundled ONNX model (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature): warns on stderr, never fails the boot.
/// </summary>
internal static class EmbeddingBootstrap
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs the ensure delegate under a 30 s bound and writes a warning when assets are unavailable; never throws.</summary>
    internal static async Task EnsureAtStartupAsync(TextWriter stderr,
        Func<CancellationToken, Task<BundledModelResult>> ensure, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            var result = await ensure(cts.Token).ConfigureAwait(false);
            if (!result.AllPresent)
            {
                await stderr.WriteLineAsync(
                    $"ai-raccoon: warning — bundled embedding model unavailable ({string.Join("; ", result.Errors)}); run 'ai-raccoon model set local' to restore it");
            }
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync(
                $"ai-raccoon: warning — bundled embedding model check failed ({ex.GetType().Name}); run 'ai-raccoon model set local' to restore it");
        }
    }
}
