namespace AiRaccoon.Hosting.Proxy;

public interface IBackendLauncher
{
    /// <summary>Returns the backend URL on the port, starting the given command when nothing answers.</summary>
    Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments,
        CancellationToken ctx);
}

/// <summary>The live backend URL, or null with the `serve` exit code when it never answered.</summary>
public readonly record struct BackendResult(string? Url, int? ServeExitCode);
