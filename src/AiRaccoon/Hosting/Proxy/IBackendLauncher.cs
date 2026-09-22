namespace AiRaccoon.Hosting.Proxy;

public interface IBackendLauncher
{
    /// <summary>
    ///     Starts a private backend that binds an ephemeral port of its own (the arguments carry
    ///     <c>--port 0</c>) and returns the URL that child printed. Nothing on a pre-existing port
    ///     is probed or contacted, so a squatter can neither receive the token nor be mistaken for
    ///     the backend (F70/K1).
    /// </summary>
    Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx);

    /// <summary>The explicit shared-server path: returns the backend already answering on the port,
    /// starting one on it when nothing answers. Reached only behind the attach opt-in.</summary>
    Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments,
        CancellationToken ctx);
}

/// <summary>The live backend URL, or null with the `serve` exit code and its captured stderr (bounded,
/// tail-only; null on the happy path) when it never answered.</summary>
public readonly record struct BackendResult(string? Url, int? ServeExitCode, string? ServeStderr = null);
