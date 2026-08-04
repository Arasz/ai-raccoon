namespace AiRaccoon.Core.Watch;

/// <summary>Watching is disabled for the project — the MCP tool maps this to `watching-disabled`.</summary>
public sealed class WatchDisabledException(string projectId) : InvalidOperationException($"Watching is disabled for project '{projectId}'.");

/// <summary>The path is outside the project's scope allowlist — the MCP tool maps this to `path-outside-scope`.</summary>
public sealed class PathOutsideScopeException(string path) : InvalidOperationException($"Path '{path}' is outside the watch scope.");

/// <summary>The watch path does not exist — the MCP tool maps this to `path-not-found`.</summary>
public sealed class PathNotFound(string path) : InvalidOperationException($"Path '{path}' does not exist.");
