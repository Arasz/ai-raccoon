namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     The install this server process started from no longer exists on disk — replaced or removed
///     out from under a still-running process (e.g. 'dotnet tool update' moving the outgoing
///     version into '.store/.stage' and deleting it while already-mapped assemblies keep serving
///     calls). The MCP tool maps this to `embedding-install-replaced`; only a restart fixes it.
/// </summary>
public sealed class BundledModelInstallReplacedException(string assetLabel, string fileName, string baseDirectory)
    : InvalidOperationException(
        $"Bundled {assetLabel} '{fileName}' could not be resolved: the install this server started from ('{baseDirectory}') no longer exists, likely replaced by a tool update (e.g. 'dotnet tool update'). Restart the MCP server (or its host) to pick up the new install.");
