namespace AiRaccoon.Hosting.Proxy;

public interface IBackendLauncher
{
    /// <summary>Returns an answering endpoint, or starts one only after confirmed connection refusal. The caller proves its identity.</summary>
    Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx);
}

/// <summary>The candidate URL, or null with the startup failure's exit code and bounded stderr.</summary>
public readonly record struct BackendResult(string? Url, int? ServeExitCode, string? ServeStderr = null);
