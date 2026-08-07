namespace AiRaccoon.Core.Watch;

/// <summary>Watching is disabled for the project — the MCP tool maps this to `watching-disabled`.</summary>
public sealed class WatchDisabledException(string projectId)
    : InvalidOperationException($"Watching is disabled for project '{projectId}'.");
