using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Best-effort teardown for a backend a WP7 settings test may have auto-started on this data
///     root (ADR-0075 §5.1): its default idle timeout is hours, so a test that triggers one has to
///     ask it to stop rather than leave it running for the rest of the suite. Silent on any failure
///     — the backend may never have started, may already be gone, or may not answer for reasons the
///     test itself already reports.
/// </summary>
internal static class RaccoonBackendCleanup
{
    internal static async Task ShutdownIfRunningAsync(string dataRoot, int port, CancellationToken cancellationToken)
    {
        var token = new McpTokenFile(dataRoot).Read();
        if (token is null)
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, token);
            await client.PostAsync($"http://127.0.0.1:{port}/shutdown", null, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Nothing left to shut down, or it will not answer — either way, not this helper's to raise.
        }
    }
}
