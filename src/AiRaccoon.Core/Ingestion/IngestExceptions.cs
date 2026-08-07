namespace AiRaccoon.Core.Ingestion;

/// <summary>The path is outside the project's ingest scope — the MCP tools map this to `path-outside-scope`.</summary>
public sealed class PathOutsideScopeException(string path)
    : InvalidOperationException($"Path '{path}' is outside the ingest scope.");

/// <summary>The path does not exist — the MCP tool maps this to `path-not-found`.</summary>
public sealed class PathNotFound(string path) : InvalidOperationException($"Path '{path}' does not exist.");
