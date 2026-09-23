using System.Diagnostics;

namespace AiRaccoon.Hosting.Proxy;

public interface IBackendLauncher
{
    /// <summary>
    ///     Starts the private fallback child that binds an ephemeral port of its own (the arguments
    ///     carry <c>--port 0</c>) and returns the URL that child printed. Nothing on a pre-existing
    ///     port is probed or contacted, so a squatter can neither receive the token nor be mistaken
    ///     for the backend (F70/K1). Reached when the configured port holds a listener that could not
    ///     prove it serves this root (ADR-0106).
    /// </summary>
    Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx);

    /// <summary>The attach-or-start mechanics: returns the backend already answering on the port,
    /// starting one on it when nothing answers. The composition root proves whatever it hands back
    /// before any token-bearing request.</summary>
    Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments,
        CancellationToken ctx);
}

/// <summary>The live backend URL, or null with the `serve` exit code and its captured stderr (bounded,
/// tail-only; null on the happy path) when it never answered. <paramref name="Child" /> is the private
/// fallback process this launcher started, so the caller can stop one that fails its proof.</summary>
public readonly record struct BackendResult(string? Url, int? ServeExitCode, string? ServeStderr = null, Process? Child = null);
